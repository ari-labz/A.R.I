using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.Voice;

public class VoiceModuleSynthesiser : ITtsSynthesiser
{
    private const int POLL_INTERVAL_MS = 150;   // short: the first sentence after a cold start waits on this
    private const int IDLE_CHECK_SECONDS = 30;

    private readonly HttpClient http;
    private readonly string modulePath;
    private readonly string voiceDir;
    private readonly string? extraArgs;
    private readonly ILogger? logger;
    private readonly int serverPort;
    private readonly int startupTimeoutSecs;
    private readonly TimeSpan idleStop;
    private readonly SemaphoreSlim launchGate = new(1, 1);
    private Timer? idleTimer;
    private DateTime lastUsedUtc = DateTime.UtcNow;
    private int inFlight;
    private Process? server;
    // True once the engine answers on its port. The process exists well before that (it may be loading or downloading its
    // models), and a request sent in that window is refused, so callers wait for this rather than for the process.
    private volatile bool ready;

    private string? engineName;
    private List<EngineParameter> parameters = [];
    private JsonElement? moduleInfo;

    // Before the engine has started and reported itself (it loads on first use), its module folder names it.
    public string EngineName => engineName ?? Path.GetFileName(modulePath.TrimEnd(Path.DirectorySeparatorChar));
    public bool LoadsOnDemand => idleStop > TimeSpan.Zero;

    /// <summary>Whether the engine process is loaded right now.</summary>
    public bool IsRunning => server is { HasExited: false };
    private bool IsReady => ready && server is { HasExited: false };
    public float Speed { get; private set; } = 1.0f;
    public float PauseScale { get; private set; } = 1.0f;

    private string SettingsPath => Path.Combine(voiceDir, "voice_settings.json");
    private string InfoCachePath => Path.Combine(voiceDir, "engine_info.json");

    public VoiceModuleSynthesiser(string modulePath, string voiceDir, int port, ILogger? logger = null,
        int startupTimeoutSecs = 120, int httpTimeoutMinutes = 5, string? extraArgs = null, int idleStopMinutes = 0)
    {
        idleStop = TimeSpan.FromMinutes(Math.Max(0, idleStopMinutes));
        this.modulePath = modulePath;
        this.voiceDir = voiceDir;
        this.serverPort = port;
        this.logger = logger;
        this.startupTimeoutSecs = startupTimeoutSecs;
        this.extraArgs = extraArgs;
        http = new HttpClient { Timeout = TimeSpan.FromMinutes(httpTimeoutMinutes) };
    }

    public IReadOnlyList<EngineParameter> GetParameters() => parameters;

    public async Task Start(CancellationToken ct = default)
    {
        LoadSettings();
        LoadCachedInfo();

        // On-demand engines launch on the first synthesis; only the saved settings and cached parameters are
        // needed up front so the UI can show them.
        if (LoadsOnDemand) return;

        // Always loaded: start now, and keep waiting if the first start has models to download.
        await Prime(ct);
    }

    private async Task LaunchAsync(CancellationToken ct)
    {
        ready = false;
        string venvPython = Path.Combine(modulePath, "venv",
            OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python3");
        string python = File.Exists(venvPython) ? venvPython : await VoiceModulePython.For(modulePath, ct);
        string script = Path.Combine(modulePath, "serve.py");

        KillPortOwner(serverPort);

        string args = $"\"{script}\" --voice-dir \"{voiceDir}\" --port {serverPort}";
        if (!string.IsNullOrEmpty(extraArgs))
            args += " " + extraArgs;

        ProcessStartInfo info = new()
        {
            FileName = python,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        server = Process.Start(info)
            ?? throw new InvalidOperationException($"Failed to start voice module server at {modulePath}.");

        _ = Task.Run(() => StreamStderr(server.StandardError), ct);
        _ = Task.Run(() => DrainStream(server.StandardOutput), ct);

        await FinishStarting(ct);
    }

    // Waits for the engine to answer, reads what it offers and starts the idle timer. Also used for an engine that was started
    // earlier but wasn't answering yet (still loading, or downloading its models the first time).
    private async Task FinishStarting(CancellationToken ct)
    {
        await WaitUntilReady(ct);
        await FetchInfo(ct);
        SaveCachedInfo();
        ready = true;

        lastUsedUtc = DateTime.UtcNow;
        if (LoadsOnDemand)
            idleTimer ??= new Timer(_ => StopWhenIdle(), null, TimeSpan.FromSeconds(IDLE_CHECK_SECONDS), TimeSpan.FromSeconds(IDLE_CHECK_SECONDS));
        logger?.LogInformation("[{Engine}] Voice engine started.", EngineName);
    }

    /// <summary>Starts the engine once so anything it fetches on first use (model weights) is downloaded now rather than in the
    /// middle of a conversation. Keeps waiting while it is still loading, for up to half an hour. An on-demand engine unloads
    /// again after its idle time.</summary>
    public async Task Prime(CancellationToken ct = default)
    {
        DateTime giveUp = DateTime.UtcNow.AddMinutes(30);
        while (true)
        {
            try { await EnsureRunning(ct); return; }
            catch (TimeoutException) when (DateTime.UtcNow < giveUp && server is { HasExited: false })
            {
                logger?.LogInformation("[{Engine}] Still loading (it may be downloading its models); waiting.", EngineName);
            }
        }
    }

    private async Task EnsureRunning(CancellationToken ct)
    {
        lastUsedUtc = DateTime.UtcNow;
        if (IsReady) return;

        // Whoever starts the engine holds the gate until it answers, so everyone else waits here instead of sending a request
        // to a port that isn't open yet.
        await launchGate.WaitAsync(ct);
        try
        {
            if (IsReady) return;
            if (server is { HasExited: false })
            {
                // Started earlier and not answering yet: wait for it rather than killing it and starting another.
                await FinishStarting(ct);
                return;
            }
            logger?.LogInformation("[{Engine}] Starting voice engine.", EngineName);
            await LaunchAsync(ct);
        }
        finally { launchGate.Release(); }
    }

    private void StopWhenIdle()
    {
        if (server is null || Volatile.Read(ref inFlight) > 0) return;
        if (DateTime.UtcNow - lastUsedUtc < idleStop) return;
        if (!launchGate.Wait(0)) return;
        try
        {
            if (server is null || Volatile.Read(ref inFlight) > 0) return;
            logger?.LogInformation("[{Engine}] Stopping idle voice engine after {Minutes} min.", EngineName, idleStop.TotalMinutes);
            ready = false;
            try { server.Kill(entireProcessTree: true); } catch { }
            server.Dispose();
            server = null;
        }
        finally { launchGate.Release(); }
    }

    private void LoadCachedInfo()
    {
        try
        {
            if (!File.Exists(InfoCachePath)) return;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(InfoCachePath));
            if (engineName is null && doc.RootElement.TryGetProperty("engine", out JsonElement eng))
                engineName = eng.GetString();
            if (parameters.Count == 0 && doc.RootElement.TryGetProperty("parameters", out JsonElement parms))
                parameters = JsonSerializer.Deserialize<List<EngineParameter>>(parms.GetRawText()) ?? [];
        }
        catch (Exception ex) { logger?.LogWarning(ex, "Failed to load cached voice engine info."); }
    }

    private void SaveCachedInfo()
    {
        try
        {
            File.WriteAllText(InfoCachePath, JsonSerializer.Serialize(new { engine = engineName, parameters }));
        }
        catch (Exception ex) { logger?.LogWarning(ex, "Failed to cache voice engine info."); }
    }

    private async Task FetchInfo(CancellationToken ct)
    {
        try
        {
            HttpResponseMessage resp = await http.GetAsync($"http://localhost:{serverPort}/info", ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(json);
            moduleInfo = doc.RootElement.Clone();

            if (moduleInfo.Value.TryGetProperty("engine", out JsonElement eng))
                engineName = eng.GetString();

            if (moduleInfo.Value.TryGetProperty("parameters", out JsonElement parms))
            {
                parameters = [];
                foreach (JsonElement p in parms.EnumerateArray())
                {
                    parameters.Add(new EngineParameter(
                        p.GetProperty("name").GetString()!,
                        p.GetProperty("label").GetString()!,
                        p.TryGetProperty("min", out JsonElement mn) ? mn.GetSingle() : 0,
                        p.TryGetProperty("max", out JsonElement mx) ? mx.GetSingle() : 1,
                        p.TryGetProperty("default", out JsonElement df) ? df.GetSingle() : 0,
                        p.TryGetProperty("step", out JsonElement st) ? st.GetSingle() : 0.1f
                    ));
                }
            }

            logger?.LogInformation("[{Engine}] Module info loaded: {Params} parameters", EngineName, parameters.Count);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to fetch /info from voice module");
        }
    }

    public bool IsTrainable =>
        moduleInfo?.TryGetProperty("type", out JsonElement t) == true && t.GetString() == "trainable";

    public async Task<bool> CheckHealth()
    {
        if (LoadsOnDemand && server is not { HasExited: false }) return true;
        try
        {
            return (await http.GetAsync($"http://localhost:{serverPort}/health")).IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task Warmup(CancellationToken ct = default)
        => await Synthesise("Voice synthesis is ready.", ct);

    public Task<byte[]> Synthesise(string text, CancellationToken ct = default)
        => Synthesise(text, null, ct);

    public async Task<byte[]> Synthesise(string text, Dictionary<string, object>? engineParams, CancellationToken ct = default)
    {
        Dictionary<string, object> payload = new Dictionary<string, object> { ["text"] = text };

        if (engineParams != null)
        {
            foreach (KeyValuePair<string, object> kvp in engineParams)
                payload[kvp.Key] = kvp.Value;
        }

        // Apply speed/pauseScale defaults from settings if not overridden
        if (!payload.ContainsKey("speed"))
            payload["speed"] = Speed;
        if (!payload.ContainsKey("pause_scale"))
            payload["pause_scale"] = PauseScale;

        string json = JsonSerializer.Serialize(payload);
        using StringContent body = new(json, Encoding.UTF8, "application/json");

        Interlocked.Increment(ref inFlight);
        try
        {
            await EnsureRunning(ct);
            HttpResponseMessage response = await http.PostAsync(
                $"http://localhost:{serverPort}/synthesise", body, ct);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        finally
        {
            lastUsedUtc = DateTime.UtcNow;
            Interlocked.Decrement(ref inFlight);
        }
    }

    public async Task LoadCheckpoint(string checkpointPath, CancellationToken ct = default)
    {
        await EnsureRunning(ct);
        string payload = JsonSerializer.Serialize(new { path = checkpointPath });
        using StringContent body = new(payload, Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await http.PostAsync(
            $"http://localhost:{serverPort}/load_model", body, ct);
        resp.EnsureSuccessStatusCode();
    }

    public void SaveSettings(float speed, float pauseScale)
    {
        Speed = speed;
        PauseScale = pauseScale;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { speed, pauseScale }));
        }
        catch (Exception ex) { logger?.LogWarning(ex, "[{Engine}] Failed to persist voice settings.", EngineName); }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("speed", out JsonElement s)) Speed = s.GetSingle();
            if (doc.RootElement.TryGetProperty("pauseScale", out JsonElement p)) PauseScale = p.GetSingle();
        }
        catch (Exception ex) { logger?.LogWarning(ex, "[{Engine}] Failed to load voice settings.", EngineName); }
    }

    public void Dispose()
    {
        idleTimer?.Dispose();
        http.Dispose();
        try { server?.Kill(entireProcessTree: true); } catch { }
        server?.Dispose();
    }

    private static void KillPortOwner(int port)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-c \"lsof -ti:{port} | xargs kill -9 2>/dev/null; true\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })?.WaitForExit(3000);
        }
        catch { }
    }

    private async Task WaitUntilReady(CancellationToken ct)
    {
        string healthUrl = $"http://localhost:{serverPort}/health";
        int elapsedMs = 0;

        while (elapsedMs < startupTimeoutSecs * 1000)
        {
            await Task.Delay(POLL_INTERVAL_MS, ct);
            elapsedMs += POLL_INTERVAL_MS;

            try
            {
                if ((await http.GetAsync(healthUrl, ct)).IsSuccessStatusCode) return;
            }
            catch { }

            if (server?.HasExited == true)
                throw new InvalidOperationException($"{EngineName} server exited unexpectedly (code {server.ExitCode}).");
        }

        throw new TimeoutException($"{EngineName} server did not become ready within {startupTimeoutSecs}s.");
    }

    private async Task StreamStderr(StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("HTTP/1.1")) continue;

            if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                logger?.LogError("[{Engine}] {Line}", EngineName, line);
            else if (line.StartsWith("[synthesise]", StringComparison.Ordinal))
                logger?.LogDebug("[{Engine}] {Line}", EngineName, line);
            else if (line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
                logger?.LogDebug("[{Engine}] {Line}", EngineName, line);
            else
                logger?.LogInformation("[{Engine}] {Line}", EngineName, line);
        }
    }

    private static async Task DrainStream(StreamReader reader)
    {
        while (await reader.ReadLineAsync() != null) { }
    }
}

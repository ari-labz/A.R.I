using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.Voice;

public class VoiceModuleSynthesiser : ITtsSynthesiser
{
    private const int POLL_INTERVAL_MS = 2000;

    private readonly HttpClient http;
    private readonly string modulePath;
    private readonly string voiceDir;
    private readonly string? extraArgs;
    private readonly ILogger? logger;
    private readonly int serverPort;
    private readonly int startupTimeoutSecs;
    private Process? server;

    private string? engineName;
    private List<EngineParameter> parameters = [];
    private JsonElement? moduleInfo;

    public string EngineName => engineName ?? "Unknown";
    public float Speed { get; private set; } = 1.0f;
    public float PauseScale { get; private set; } = 1.0f;

    private string SettingsPath => Path.Combine(voiceDir, "voice_settings.json");

    public VoiceModuleSynthesiser(string modulePath, string voiceDir, int port, ILogger? logger = null,
        int startupTimeoutSecs = 120, int httpTimeoutMinutes = 5, string? extraArgs = null)
    {
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

        string venvPython = Path.Combine(modulePath, "venv",
            OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python3");
        string python = File.Exists(venvPython) ? venvPython : FindSystemPython();
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

        await WaitUntilReady(ct);
        await FetchInfo(ct);
    }

    private async Task FetchInfo(CancellationToken ct)
    {
        try
        {
            var resp = await http.GetAsync($"http://localhost:{serverPort}/info", ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            moduleInfo = doc.RootElement.Clone();

            if (moduleInfo.Value.TryGetProperty("engine", out var eng))
                engineName = eng.GetString();

            if (moduleInfo.Value.TryGetProperty("parameters", out var parms))
            {
                parameters = [];
                foreach (var p in parms.EnumerateArray())
                {
                    parameters.Add(new EngineParameter(
                        p.GetProperty("name").GetString()!,
                        p.GetProperty("label").GetString()!,
                        p.TryGetProperty("min", out var mn) ? mn.GetSingle() : 0,
                        p.TryGetProperty("max", out var mx) ? mx.GetSingle() : 1,
                        p.TryGetProperty("default", out var df) ? df.GetSingle() : 0,
                        p.TryGetProperty("step", out var st) ? st.GetSingle() : 0.1f
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
        moduleInfo?.TryGetProperty("type", out var t) == true && t.GetString() == "trainable";

    public async Task<bool> CheckHealth()
    {
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
        var payload = new Dictionary<string, object> { ["text"] = text };

        if (engineParams != null)
        {
            foreach (var kvp in engineParams)
                payload[kvp.Key] = kvp.Value;
        }

        // Apply speed/pauseScale defaults from settings if not overridden
        if (!payload.ContainsKey("speed"))
            payload["speed"] = Speed;
        if (!payload.ContainsKey("pause_scale"))
            payload["pause_scale"] = PauseScale;

        string json = JsonSerializer.Serialize(payload);
        using StringContent body = new(json, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(
            $"http://localhost:{serverPort}/synthesise", body, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task LoadCheckpoint(string checkpointPath, CancellationToken ct = default)
    {
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
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("speed", out var s)) Speed = s.GetSingle();
            if (doc.RootElement.TryGetProperty("pauseScale", out var p)) PauseScale = p.GetSingle();
        }
        catch (Exception ex) { logger?.LogWarning(ex, "[{Engine}] Failed to load voice settings.", EngineName); }
    }

    public void Dispose()
    {
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
        int elapsed = 0;

        while (elapsed < startupTimeoutSecs)
        {
            await Task.Delay(POLL_INTERVAL_MS, ct);
            elapsed += POLL_INTERVAL_MS / 1000;

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

    private static string FindSystemPython()
    {
        foreach (string candidate in new[] { "python3", "python" })
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo(candidate, "--version")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                proc?.WaitForExit(3000);
                if (proc?.ExitCode == 0) return candidate;
            }
            catch { }
        }
        return "python3";
    }
}

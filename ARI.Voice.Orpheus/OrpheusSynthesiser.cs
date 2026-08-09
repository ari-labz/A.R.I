using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ARI.Common;
using ARI.Voice;
using Microsoft.Extensions.Logging;

namespace ARI.Voice.Orpheus;

public class OrpheusSynthesiser(string orpheusPath, string modelPath, ILogger? logger = null) : ITtsSynthesiser
{
    private const string SERVER_SCRIPT        = "serve.py";
    private const int    SERVER_PORT          = 8021;
    private const int    LLAMA_PORT           = 8024;
    private const int    POLL_INTERVAL_MS     = 2000;
    private const int    STARTUP_TIMEOUT_SECS = 180;

    private static readonly IReadOnlyList<EngineParameter> Parameters =
    [
        new("temperature",       "Temperature",        0.1f, 2.0f, 0.6f,  0.05f),
        new("topP",              "Top P",              0.0f, 1.0f, 0.9f,  0.05f),
        new("repetitionPenalty", "Repetition Penalty", 1.0f, 2.0f, 1.1f,  0.05f),
        new("maxTokens",         "Max Tokens",         200f, 4000f, 1200f, 100f),
    ];

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private Process? server;

    public string EngineName => "Orpheus";
    public float Speed      { get; private set; } = 1.0f;
    public float PauseScale { get; private set; } = 1.0f;

    private string SettingsPath => Path.Combine(Path.GetDirectoryName(modelPath) ?? "", "voice_settings.json");

    public IReadOnlyList<EngineParameter> GetParameters() => Parameters;

    public void SaveSettings(float speed, float pauseScale)
    {
        Speed = speed; PauseScale = pauseScale;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { speed, pauseScale }));
        }
        catch (Exception ex) { logger?.LogWarning(ex, "[Orpheus] Failed to persist voice settings."); }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (doc.RootElement.TryGetProperty("speed", out var s)) Speed = s.GetSingle();
                if (doc.RootElement.TryGetProperty("pauseScale", out var p)) PauseScale = p.GetSingle();
            }
        }
        catch (Exception ex) { logger?.LogWarning(ex, "[Orpheus] Failed to load voice settings."); }
    }

    public async Task Start(CancellationToken ct = default)
    {
        LoadSettings();

        string python = await EnsureVenv(ct);
        string script = Path.Combine(orpheusPath, SERVER_SCRIPT);

        KillPortOwner(SERVER_PORT);
        KillPortOwner(LLAMA_PORT);

        ProcessStartInfo info = new()
        {
            FileName               = python,
            Arguments              = $"\"{script}\" --model \"{modelPath}\" --port {SERVER_PORT} --llama-port {LLAMA_PORT} --llama-exe \"{Shared.LlamaServer}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        server = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start Orpheus inference server.");

        _ = Task.Run(() => StreamOutput(server.StandardError), ct);
        _ = Task.Run(() => StreamOutput(server.StandardOutput), ct);

        await WaitUntilReady(ct);
    }

    public async Task<bool> CheckHealth()
    {
        try
        {
            var r = await http.GetAsync($"http://localhost:{SERVER_PORT}/health");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task Warmup(CancellationToken ct = default)
    {
        await Synthesise("Voice synthesis is ready.", ct);
    }

    public Task<byte[]> Synthesise(string text, CancellationToken ct = default)
        => Synthesise(text, null, ct);

    public async Task<byte[]> Synthesise(string text, Dictionary<string, object>? engineParams, CancellationToken ct = default)
    {
        float temperature     = GetParam(engineParams, "temperature", 0.6f);
        float topP            = GetParam(engineParams, "topP", 0.9f);
        float repetitionPen   = GetParam(engineParams, "repetitionPenalty", 1.1f);
        int   maxTokens       = GetParam(engineParams, "maxTokens", 1200);
        string? voice         = GetParam<string?>(engineParams, "voice", null);

        string url = $"http://localhost:{SERVER_PORT}/synthesise";
        string payload = JsonSerializer.Serialize(new
        {
            text,
            voice         = voice,
            temperature,
            top_p         = topP,
            repetition_penalty = repetitionPen,
            max_tokens    = maxTokens,
        });

        using StringContent body = new(payload, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(url, body, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public void Dispose()
    {
        http.Dispose();
        try { server?.Kill(entireProcessTree: true); } catch { }
        server?.Dispose();
    }

    private static T GetParam<T>(Dictionary<string, object>? p, string key, T fallback)
    {
        if (p is null || !p.TryGetValue(key, out object? val)) return fallback;
        try { return (T)Convert.ChangeType(val, typeof(T)); }
        catch { return fallback; }
    }

    private async Task<string> EnsureVenv(CancellationToken ct)
    {
        string venvDir = Path.Combine(orpheusPath, "venv");
        string venvPython = Path.Combine(venvDir, OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python3");

        if (File.Exists(venvPython))
        {
            try
            {
                var check = Process.Start(new ProcessStartInfo
                {
                    FileName = venvPython, Arguments = "--version",
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                });
                check?.WaitForExit(5000);
                if (check?.ExitCode == 0) return venvPython;
            }
            catch { }
            logger?.LogWarning("[Orpheus] Existing venv is broken, recreating");
            Directory.Delete(venvDir, recursive: true);
        }

        string systemPython = FindSystemPython();

        logger?.LogInformation("[Orpheus] Creating venv...");
        var venvProc = Process.Start(new ProcessStartInfo
        {
            FileName = systemPython, Arguments = $"-m venv \"{venvDir}\"",
            RedirectStandardError = true, UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to create Orpheus venv");
        await venvProc.WaitForExitAsync(ct);
        if (venvProc.ExitCode != 0)
            throw new Exception($"Orpheus venv creation failed: {await venvProc.StandardError.ReadToEndAsync(ct)}");

        logger?.LogInformation("[Orpheus] Installing inference dependencies...");
        string reqPath = Path.Combine(orpheusPath, "requirements.txt");
        var pip = Process.Start(new ProcessStartInfo
        {
            FileName = venvPython, Arguments = $"-m pip install -r \"{reqPath}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = orpheusPath,
        }) ?? throw new InvalidOperationException("Failed to run pip for Orpheus");

        _ = Task.Run(async () => { while (await pip.StandardOutput.ReadLineAsync(ct) is { } line) logger?.LogDebug("[Orpheus-Setup] {Line}", line); }, ct);
        _ = Task.Run(async () => { while (await pip.StandardError.ReadLineAsync(ct) is { } line) logger?.LogWarning("[Orpheus-Setup] {Line}", line); }, ct);
        await pip.WaitForExitAsync(ct);
        if (pip.ExitCode != 0)
            throw new Exception("Orpheus pip install failed");

        return venvPython;
    }

    private static string FindSystemPython()
    {
        foreach (string candidate in new[] { "python3", "python" })
        {
            try
            {
                var psi = new ProcessStartInfo(candidate, "--version")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                var proc = Process.Start(psi);
                proc?.WaitForExit(3000);
                if (proc?.ExitCode == 0) return candidate;
            }
            catch { }
        }
        return "python3";
    }

    private static void KillPortOwner(int port)
    {
        try
        {
            ProcessStartInfo info = new()
            {
                FileName               = "bash",
                Arguments              = $"-c \"lsof -ti:{port} | xargs kill -9 2>/dev/null; true\"",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            Process.Start(info)?.WaitForExit(3000);
        }
        catch { }
    }

    private async Task WaitUntilReady(CancellationToken ct)
    {
        string healthUrl = $"http://localhost:{SERVER_PORT}/health";
        int elapsed = 0;

        while (elapsed < STARTUP_TIMEOUT_SECS)
        {
            await Task.Delay(POLL_INTERVAL_MS, ct);
            elapsed += POLL_INTERVAL_MS / 1000;

            try
            {
                HttpResponseMessage resp = await http.GetAsync(healthUrl, ct);
                if (resp.IsSuccessStatusCode) return;
            }
            catch { }

            if (server?.HasExited == true)
                throw new InvalidOperationException($"Orpheus server exited unexpectedly (code {server.ExitCode}).");
        }

        throw new TimeoutException($"Orpheus server did not become ready within {STARTUP_TIMEOUT_SECS}s.");
    }

    private async Task StreamOutput(StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("HTTP/1.1")) continue;

            if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                logger?.LogError("[Orpheus] {Line}", line);
            else if (line.StartsWith("[synthesise]", StringComparison.Ordinal) || line.StartsWith("[Orpheus]", StringComparison.Ordinal))
                logger?.LogDebug("[Orpheus] {Line}", line);
            else
                logger?.LogInformation("[Orpheus] {Line}", line);
        }
    }
}

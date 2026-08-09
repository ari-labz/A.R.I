using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ARI.Common;
using ARI.Voice;
using Microsoft.Extensions.Logging;

namespace ARI.Voice.IndexTTS;

/// <summary>
/// IndexTTS2 speech synthesis.
/// <para>
/// Clones a voice from a single reference clip and takes the emotion as a
/// separate input, so one voice can deliver the same words a dozen different
/// ways. Text may carry inline tags — <c>[curious] … [angry:0.6] …</c> — and
/// each tagged span is spoken with its own feeling, letting a single reply
/// change mood part-way through.
/// </para>
/// <para>
/// It is slow: generating speech takes roughly twice as long as the speech
/// lasts, so it suits lines prepared ahead of time far better than live
/// conversation. StyleTTS2 remains the quicker option for talking.
/// </para>
/// </summary>
public class IndexTtsSynthesiser(string indexPath, string voiceDir, ILogger? logger = null) : ITtsSynthesiser
{
    private const string SERVER_SCRIPT        = "serve.py";
    private const int    SERVER_PORT          = 8026;
    private const int    POLL_INTERVAL_MS     = 2000;
    // First run downloads roughly six gigabytes of model weights.
    private const int    STARTUP_TIMEOUT_SECS = 1800;

    private static readonly IReadOnlyList<EngineParameter> Parameters =
    [
        // Emotion strength. Past about 0.5 the delivery starts pulling the voice
        // away from the reference clip and it stops sounding like itself.
        new("alpha", "Emotion Strength", 0.0f, 1.0f, 0.40f, 0.05f),
    ];

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private Process? server;

    public string EngineName => "IndexTTS";
    public float Speed      { get; private set; } = 1.0f;
    public float PauseScale { get; private set; } = 1.0f;

    private string SettingsPath => Path.Combine(voiceDir, "voice_settings.json");

    public IReadOnlyList<EngineParameter> GetParameters() => Parameters;

    public void SaveSettings(float speed, float pauseScale)
    {
        Speed = speed; PauseScale = pauseScale;
        try
        {
            Directory.CreateDirectory(voiceDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { speed, pauseScale }));
        }
        catch (Exception ex) { logger?.LogWarning(ex, "[IndexTTS] Failed to persist voice settings."); }
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
        catch (Exception ex) { logger?.LogWarning(ex, "[IndexTTS] Failed to load voice settings."); }
    }

    public async Task Start(CancellationToken ct = default)
    {
        LoadSettings();

        string python = await EnsureVenv(ct);
        string repo   = await EnsureRepo(python, ct);
        string script = Path.Combine(indexPath, SERVER_SCRIPT);

        KillPortOwner(SERVER_PORT);

        ProcessStartInfo info = new()
        {
            FileName               = python,
            Arguments              = $"\"{script}\" --voice-dir \"{voiceDir}\" --repo \"{repo}\" --port {SERVER_PORT}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        server = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start IndexTTS inference server.");

        _ = Task.Run(() => StreamOutput(server.StandardError), ct);
        _ = Task.Run(() => StreamOutput(server.StandardOutput), ct);

        await WaitUntilReady(ct);
    }

    public async Task<bool> CheckHealth()
    {
        try
        {
            return (await http.GetAsync($"http://localhost:{SERVER_PORT}/health")).IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task Warmup(CancellationToken ct = default)
        => await Synthesise("Voice synthesis is ready.", ct);

    public Task<byte[]> Synthesise(string text, CancellationToken ct = default)
        => Synthesise(text, null, ct);

    public async Task<byte[]> Synthesise(string text, Dictionary<string, object>? engineParams, CancellationToken ct = default)
    {
        float   alpha   = GetParam(engineParams, "alpha", 0.40f);
        // Applies to any text outside an inline tag; tags override it per span.
        string? emotion = GetParam<string?>(engineParams, "emotion", null);

        string payload = JsonSerializer.Serialize(new { text, alpha, emotion });
        using StringContent body = new(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await http.PostAsync(
            $"http://localhost:{SERVER_PORT}/synthesise", body, ct);
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

    /// <summary>Clones the index-tts checkout and installs it into the venv.</summary>
    private async Task<string> EnsureRepo(string python, CancellationToken ct)
    {
        string repo = Path.Combine(indexPath, "index-tts");
        if (Directory.Exists(Path.Combine(repo, "indextts")))
            return repo;

        logger?.LogInformation("[IndexTTS] Cloning index-tts...");
        var clone = Process.Start(new ProcessStartInfo
        {
            FileName  = "git",
            Arguments = $"clone --depth 1 https://github.com/index-tts/index-tts.git \"{repo}\"",
            RedirectStandardError = true, UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to run git clone for IndexTTS");
        await clone.WaitForExitAsync(ct);
        if (clone.ExitCode != 0)
            throw new Exception($"IndexTTS clone failed: {await clone.StandardError.ReadToEndAsync(ct)}");

        logger?.LogInformation("[IndexTTS] Installing index-tts (this downloads several GB)...");
        var install = Process.Start(new ProcessStartInfo
        {
            FileName = python, Arguments = "-m pip install -e .",
            WorkingDirectory = repo,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to install IndexTTS");
        _ = Task.Run(async () => { while (await install.StandardOutput.ReadLineAsync(ct) is { } l) logger?.LogDebug("[IndexTTS-Setup] {Line}", l); }, ct);
        await install.WaitForExitAsync(ct);
        if (install.ExitCode != 0)
            throw new Exception("IndexTTS install failed");

        return repo;
    }

    private async Task<string> EnsureVenv(CancellationToken ct)
    {
        string venvDir    = Path.Combine(indexPath, "venv");
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
            logger?.LogWarning("[IndexTTS] Existing venv is broken, recreating");
            Directory.Delete(venvDir, recursive: true);
        }

        logger?.LogInformation("[IndexTTS] Creating venv...");
        var venvProc = Process.Start(new ProcessStartInfo
        {
            FileName = FindSystemPython(), Arguments = $"-m venv \"{venvDir}\"",
            RedirectStandardError = true, UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to create IndexTTS venv");
        await venvProc.WaitForExitAsync(ct);
        if (venvProc.ExitCode != 0)
            throw new Exception($"IndexTTS venv creation failed: {await venvProc.StandardError.ReadToEndAsync(ct)}");

        string reqPath = Path.Combine(indexPath, "requirements.txt");
        var pip = Process.Start(new ProcessStartInfo
        {
            FileName = venvPython, Arguments = $"-m pip install -r \"{reqPath}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = indexPath,
        }) ?? throw new InvalidOperationException("Failed to run pip for IndexTTS");
        await pip.WaitForExitAsync(ct);
        if (pip.ExitCode != 0)
            throw new Exception("IndexTTS pip install failed");

        return venvPython;
    }

    private static string FindSystemPython()
    {
        // index-tts needs 3.10 or 3.11; a newer default python will not resolve.
        foreach (string candidate in new[] { "python3.11", "python3.10", "python3", "python" })
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

    private static void KillPortOwner(int port)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName               = "bash",
                Arguments              = $"-c \"lsof -ti:{port} | xargs kill -9 2>/dev/null; true\"",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            })?.WaitForExit(3000);
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
                if ((await http.GetAsync(healthUrl, ct)).IsSuccessStatusCode) return;
            }
            catch { }

            if (server?.HasExited == true)
                throw new InvalidOperationException($"IndexTTS server exited unexpectedly (code {server.ExitCode}).");
        }

        throw new TimeoutException($"IndexTTS server did not become ready within {STARTUP_TIMEOUT_SECS}s.");
    }

    private async Task StreamOutput(StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("HTTP/1.1")) continue;

            if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                logger?.LogError("[IndexTTS] {Line}", line);
            else if (line.StartsWith("[synthesise]", StringComparison.Ordinal) || line.StartsWith("[IndexTTS]", StringComparison.Ordinal))
                logger?.LogDebug("[IndexTTS] {Line}", line);
            else
                logger?.LogInformation("[IndexTTS] {Line}", line);
        }
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ARI.Voice;

public class StyleTtsSynthesiser(string styleTtsPath, string modelPath, string configPath, string refAudioPath, ILogger? logger = null) : IDisposable
{
    private const string SERVER_SCRIPT        = "serve.py";
    private const int    SERVER_PORT          = 8020;
    private const int    POLL_INTERVAL_MS     = 2000;
    private const int    STARTUP_TIMEOUT_SECS = 120;

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private Process? server;
    private string _currentCheckpoint = modelPath;

    // Persisted server-side defaults so the sliders in the Voice tab change Ari's real
    // conversational voice (SpeechQueue calls Speak with no overrides).
    private string SettingsPath => Path.Combine(Path.GetDirectoryName(modelPath) ?? styleTtsPath, "voice_settings.json");
    public float Speed      { get; private set; } = 1.0f;
    public float PauseScale { get; private set; } = 1.0f;

    private sealed record VoiceSettings(float Speed = 1.0f, float PauseScale = 1.0f);

    public void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                VoiceSettings? s = JsonSerializer.Deserialize<VoiceSettings>(File.ReadAllText(SettingsPath));
                if (s is not null) { Speed = s.Speed; PauseScale = s.PauseScale; }
            }
        }
        catch (Exception ex) { logger?.LogWarning(ex, "[StyleTTS2] Failed to load voice settings; using defaults."); }
    }

    public void SaveSettings(float speed, float pauseScale)
    {
        Speed = speed; PauseScale = pauseScale;
        try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new VoiceSettings(speed, pauseScale))); }
        catch (Exception ex) { logger?.LogWarning(ex, "[StyleTTS2] Failed to persist voice settings."); }
    }

    public async Task Start(CancellationToken ct = default)
    {
        LoadSettings();

        string python = Path.Combine(styleTtsPath, "venv", "bin", "python");
        string script = Path.Combine(styleTtsPath, SERVER_SCRIPT);

        KillPortOwner(SERVER_PORT);

        ProcessStartInfo info = new()
        {
            FileName               = python,
            Arguments              = $"\"{script}\" --model \"{modelPath}\" --config \"{configPath}\" --ref_audio \"{refAudioPath}\" --port {SERVER_PORT}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        server = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start StyleTTS2 inference server.");

        _ = Task.Run(() => StreamErrors(server.StandardError), ct);
        _ = Task.Run(() => DrainOutput(server.StandardOutput), ct);

        await WaitUntilReady(ct);
    }

    public async Task Warmup(CancellationToken ct = default)
    {
        await Speak("Voice synthesis is ready.", ct);
    }

    public async Task<byte[]> Speak(string text, CancellationToken ct = default, int diffusionSteps = 5, float alpha = 0.3f, float beta = 0.7f, float embeddingScale = 1.0f, float? speed = null, float? pauseScale = null)
    {
        // C# can't default a parameter to an instance field, so resolve the persisted defaults here.
        float resolvedSpeed = speed      ?? Speed;
        float resolvedPause = pauseScale ?? PauseScale;

        string url     = $"http://localhost:{SERVER_PORT}/synthesise";
        string payload = JsonSerializer.Serialize(new { text, diffusion_steps = diffusionSteps, alpha, beta, embedding_scale = embeddingScale, speed = resolvedSpeed, pause_scale = resolvedPause });

        using StringContent body = new(payload, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(url, body, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]> SpeakWithCheckpoint(string text, string checkpointPath, CancellationToken ct = default, int diffusionSteps = 5, float alpha = 0.3f, float beta = 0.7f, float embeddingScale = 1.0f, float? speed = null, float? pauseScale = null)
    {
        if (_currentCheckpoint != checkpointPath)
        {
            string loadUrl     = $"http://localhost:{SERVER_PORT}/load_model";
            string loadPayload = JsonSerializer.Serialize(new { path = checkpointPath });
            using StringContent loadBody = new(loadPayload, Encoding.UTF8, "application/json");
            HttpResponseMessage loadResp = await http.PostAsync(loadUrl, loadBody, ct);
            loadResp.EnsureSuccessStatusCode();
            _currentCheckpoint = checkpointPath;
            logger?.LogInformation("[StyleTTS2] Hot-swapped checkpoint to {Path}", checkpointPath);
        }
        return await Speak(text, ct, diffusionSteps, alpha, beta, embeddingScale, speed, pauseScale);
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
        string healthUrl   = $"http://localhost:{SERVER_PORT}/health";
        int    elapsedSecs = 0;

        while (elapsedSecs < STARTUP_TIMEOUT_SECS)
        {
            await Task.Delay(POLL_INTERVAL_MS, ct);
            elapsedSecs += POLL_INTERVAL_MS / 1000;

            try
            {
                HttpResponseMessage resp = await http.GetAsync(healthUrl, ct);
                if (resp.IsSuccessStatusCode) return;
            }
            catch { /* not ready yet */ }

            if (server?.HasExited == true)
                throw new InvalidOperationException($"StyleTTS2 server exited unexpectedly (code {server.ExitCode}).");
        }

        throw new TimeoutException($"StyleTTS2 server did not become ready within {STARTUP_TIMEOUT_SECS}s.");
    }

    private async Task StreamErrors(StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("WARNING", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("HTTP/1.1")) continue;
            logger?.LogWarning("[StyleTTS2] {Line}", line);
        }
    }

    private static async Task DrainOutput(StreamReader reader)
    {
        while (await reader.ReadLineAsync() != null) { }
    }
}

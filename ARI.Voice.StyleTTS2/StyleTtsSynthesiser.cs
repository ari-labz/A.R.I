using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ARI.Common;
using ARI.Voice;
using Microsoft.Extensions.Logging;

namespace ARI.Voice.StyleTTS2;

public class StyleTtsSynthesiser(string styleTtsPath, string dataDir, string modelPath, string configPath, string refAudioPath, ILogger? logger = null) : ITtsSynthesiser
{
    private const string SERVER_SCRIPT        = "serve.py";
    private const int    SERVER_PORT          = 8021;
    private const int    POLL_INTERVAL_MS     = 2000;
    private const int    STARTUP_TIMEOUT_SECS = 120;

    private static readonly IReadOnlyList<EngineParameter> Parameters =
    [
        new("diffusionSteps", "Diffusion Steps", 1, 20, 10, 1),
        new("alpha",          "Alpha",           0, 1,  0.45f, 0.05f),
        new("beta",           "Beta",            0, 1,  0.25f, 0.05f),
        new("embeddingScale", "Embedding Scale",  0, 5,  2.0f,  0.1f),
    ];

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private Process? server;
    private string _currentCheckpoint = modelPath;

    public string EngineName => "StyleTTS2";

    private string SettingsPath => Path.Combine(Path.GetDirectoryName(modelPath) ?? dataDir, "voice_settings.json");
    public float Speed      { get; private set; } = 1.0f;
    public float PauseScale { get; private set; } = 2.3f;

    private sealed record VoiceSettings(float Speed = 1.0f, float PauseScale = 1.0f);

    public IReadOnlyList<EngineParameter> GetParameters() => Parameters;

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

        string python = Paths.StyleTts2Python;
        string script = Path.Combine(styleTtsPath, SERVER_SCRIPT);

        KillPortOwner(SERVER_PORT);

        string subsArg = PhonemeSubstitutions.Path is { } p ? $" --phoneme_subs \"{p}\"" : "";

        ProcessStartInfo info = new()
        {
            FileName               = python,
            Arguments              = $"\"{script}\" --model \"{modelPath}\" --config \"{configPath}\" --ref_audio \"{refAudioPath}\" --port {SERVER_PORT}{subsArg} --cpu",
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
        int   diffusionSteps = GetParam<int>(engineParams, "diffusionSteps", 10);
        float alpha          = GetParam<float>(engineParams, "alpha", 0.45f);
        float beta           = GetParam<float>(engineParams, "beta", 0.25f);
        float embeddingScale = GetParam<float>(engineParams, "embeddingScale", 2.0f);
        float? speed         = GetParamNullable<float>(engineParams, "speed");
        float? pauseScale    = GetParamNullable<float>(engineParams, "pauseScale");
        string? checkpoint   = GetParam<string?>(engineParams, "checkpointPath", null);

        if (!string.IsNullOrEmpty(checkpoint))
            return await SpeakWithCheckpoint(text, checkpoint, ct, diffusionSteps, alpha, beta, embeddingScale, speed, pauseScale);

        return await Speak(text, ct, diffusionSteps, alpha, beta, embeddingScale, speed, pauseScale);
    }

    public async Task<byte[]> Speak(string text, CancellationToken ct, int diffusionSteps = 10, float alpha = 0.45f, float beta = 0.25f, float embeddingScale = 2.0f, float? speed = null, float? pauseScale = null)
    {
        float resolvedSpeed = speed      ?? Speed;
        float resolvedPause = pauseScale ?? PauseScale;

        string url     = $"http://localhost:{SERVER_PORT}/synthesise";
        string payload = JsonSerializer.Serialize(new { text, diffusion_steps = diffusionSteps, alpha, beta, embedding_scale = embeddingScale, speed = resolvedSpeed, pause_scale = resolvedPause });

        using StringContent body = new(payload, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(url, body, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]> SpeakWithCheckpoint(string text, string checkpointPath, CancellationToken ct = default, int diffusionSteps = 10, float alpha = 0.45f, float beta = 0.25f, float embeddingScale = 2.0f, float? speed = null, float? pauseScale = null)
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

    private static T GetParam<T>(Dictionary<string, object>? p, string key, T fallback)
    {
        if (p is null || !p.TryGetValue(key, out object? val)) return fallback;
        try { return (T)Convert.ChangeType(val, typeof(T)); }
        catch { return fallback; }
    }

    private static T? GetParamNullable<T>(Dictionary<string, object>? p, string key) where T : struct
    {
        if (p is null || !p.TryGetValue(key, out object? val)) return null;
        try { return (T)Convert.ChangeType(val, typeof(T)); }
        catch { return null; }
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
            catch { }

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
            if (line.Contains("HTTP/1.1")) continue;
            if (line.Contains("WARNING", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith(" ", StringComparison.Ordinal)) continue;

            if (line.StartsWith("[synthesise]", StringComparison.Ordinal))
            {
                if (line.Contains("ERROR", StringComparison.Ordinal)) logger?.LogError("[StyleTTS2] {Line}", line);
                else                                                  logger?.LogDebug("[StyleTTS2] {Line}", line);
                continue;
            }

            logger?.LogWarning("[StyleTTS2] {Line}", line);
        }
    }

    private static async Task DrainOutput(StreamReader reader)
    {
        while (await reader.ReadLineAsync() != null) { }
    }
}

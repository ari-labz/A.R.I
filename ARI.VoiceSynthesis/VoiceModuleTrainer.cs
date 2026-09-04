using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

public class VoiceModuleTrainer : IVoiceTrainer
{
    private const int HTTP_TIMEOUT_MIN = 30;
    private const int POLL_INTERVAL_MS = 2000;

    private readonly string baseUrl;
    private readonly string audioPath;
    private readonly string voiceDir;
    private readonly string modelName;
    private readonly int epochs;
    private readonly int saveEvery;
    private readonly bool retrain;
    private readonly Dictionary<string, string>? transcripts;
    private readonly string? phonemeSubs;
    private readonly ILogger? logger;
    private readonly HttpClient http;

    public VoiceModuleTrainer(string baseUrl, string audioPath, string voiceDir,
        string modelName = "finetuned", int epochs = 50, int saveEvery = 5,
        bool retrain = false, Dictionary<string, string>? transcripts = null, string? phonemeSubs = null,
        ILogger? logger = null)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.audioPath = audioPath;
        this.voiceDir = voiceDir;
        this.modelName = modelName;
        this.epochs = epochs;
        this.saveEvery = saveEvery;
        this.retrain = retrain;
        this.transcripts = transcripts;
        this.phonemeSubs = phonemeSubs;
        this.logger = logger;
        http = new HttpClient { Timeout = TimeSpan.FromMinutes(HTTP_TIMEOUT_MIN) };
    }

    public async Task<string> Train(IProgress<TrainingProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new TrainingProgress("Starting", 0, "Sending training request to module"));

        Dictionary<string, object> payload = new Dictionary<string, object>
        {
            ["voice_dir"] = voiceDir,
            ["audio_path"] = audioPath,
            ["model_name"] = modelName,
            ["epochs"] = epochs,
            ["save_every"] = saveEvery,
            ["retrain"] = retrain,
        };
        if (transcripts != null) payload["transcripts"] = JsonSerializer.Serialize(transcripts);
        if (phonemeSubs != null) payload["phoneme_subs"] = phonemeSubs;

        string json = JsonSerializer.Serialize(payload);
        using StringContent body = new(json, Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await http.PostAsync($"{baseUrl}/train", body, ct);
        resp.EnsureSuccessStatusCode();

        progress?.Report(new TrainingProgress("Training", 5, "Training started"));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(POLL_INTERVAL_MS, ct);

                try
                {
                    HttpResponseMessage statusResp = await http.GetAsync($"{baseUrl}/train/status", ct);
                    statusResp.EnsureSuccessStatusCode();
                    string statusJson = await statusResp.Content.ReadAsStringAsync(ct);
                    using JsonDocument doc = JsonDocument.Parse(statusJson);
                    JsonElement root = doc.RootElement;

                    string status = root.GetProperty("status").GetString() ?? "unknown";

                    if (root.TryGetProperty("progress", out JsonElement prog))
                    {
                        string step = prog.TryGetProperty("step", out JsonElement s) ? s.GetString() ?? "Training" : "Training";
                        int percent = prog.TryGetProperty("percent", out JsonElement pct) ? pct.GetInt32() : 0;
                        string? detail = prog.TryGetProperty("detail", out JsonElement d) ? d.GetString() : null;
                        progress?.Report(new TrainingProgress(step, percent, detail));
                    }

                    if (root.TryGetProperty("log", out JsonElement logs))
                    {
                        foreach (JsonElement line in logs.EnumerateArray())
                        {
                            string? text = line.GetString();
                            if (text == null) continue;
                            logger?.LogInformation("[Training] {Line}", text);
                            // Forward every log line to the UI so the graph and log widget stay live.
                            // LOSS_JSON lines are parsed by the control panel JS.
                            progress?.Report(new TrainingProgress("Training", -1, text));
                        }
                    }

                    switch (status)
                    {
                        case "completed":
                            progress?.Report(new TrainingProgress("Complete", 100, "Training finished"));
                            return "completed";
                        case "failed":
                            int exitCode = root.TryGetProperty("exit_code", out JsonElement ec) ? ec.GetInt32() : -1;
                            throw new Exception($"Training failed with exit code {exitCode}");
                        case "paused":
                            progress?.Report(new TrainingProgress("Paused", -1, "Training paused — resume to continue"));
                            return "paused";
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to poll training status");
                }
            }
        }
        catch (OperationCanceledException)
        {
            try { await Cancel(); } catch { }
            throw;
        }

        return "cancelled";
    }

    public async Task Pause()
    {
        HttpResponseMessage resp = await http.PostAsync($"{baseUrl}/train/pause", null);
        resp.EnsureSuccessStatusCode();
    }

    public async Task Resume()
    {
        HttpResponseMessage resp = await http.PostAsync($"{baseUrl}/train/resume", null);
        resp.EnsureSuccessStatusCode();
    }

    public async Task Cancel()
    {
        HttpResponseMessage resp = await http.PostAsync($"{baseUrl}/train/cancel", null);
        resp.EnsureSuccessStatusCode();
    }
}

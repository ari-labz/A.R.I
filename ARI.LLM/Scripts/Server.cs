using ARI.Common;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public enum ServerStatus { Offline, Starting, Online, Stopping }

/// <summary>
/// A single llama-server instance. Config properties are persisted in PersistentData;
/// runtime state (Status, Pid, ActiveModel) lives only while ARI is running.
/// Call StartAsync to boot the process; the server then self-manages via Stop/Restart/ChangeModel.
/// </summary>
public class Server : IDisposable
{
    // ── Persisted config ────────────────────────────────────────────────────────

    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "http://127.0.0.1";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 8081;

    [JsonPropertyName("kvCache")]
    public int ContextSize { get; set; } = 32768;

    [JsonPropertyName("kvCacheQuantK")]
    public int KvCacheQuantK { get; set; } = 4;

    [JsonPropertyName("kvCacheQuantV")]
    public int KvCacheQuantV { get; set; } = 4;

    [JsonPropertyName("parallelSlots")]
    public int ParallelSlots { get; set; } = 1;

    /// <summary>Model this server loads on startup — matches Model.Name in PersistentData.</summary>
    [JsonPropertyName("currentModelName")]
    public string? CurrentModelName { get; set; }

    [JsonPropertyName("bootStartup")]
    public bool BootStartup { get; set; } = true;

    [JsonPropertyName("unifiedCache")]
    public bool UnifiedCache { get; set; } = false;

    /// <summary>Load the model's multimodal projector (--mmproj). Off by default: vision costs RAM
    /// the coding/dialogue path never uses, and on unified memory that competes with weights + KV.</summary>
    [JsonPropertyName("visionEnabled")]
    public bool VisionEnabled { get; set; } = false;

    // ── Runtime state (not persisted) ───────────────────────────────────────────

    [JsonIgnore] public ServerStatus Status { get; private set; } = ServerStatus.Offline;
    [JsonIgnore] public int Pid { get; private set; } = -1;
    [JsonIgnore] public Model? ActiveModel { get; private set; }

    [JsonIgnore] public string FullEndpoint => $"{Endpoint}:{Port}";

    // ── Internals ────────────────────────────────────────────────────────────────

    private ILogger? _logger;
    private Process? _process;
    private string _modelsPath = "";

    public void SetLogger(ILogger logger) => _logger = logger;

    private ILogger Log => _logger ?? Shared.Logger;

    // ── Public lifecycle ─────────────────────────────────────────────────────────

    public async Task StartAsync(Model? model, string modelsPath)
    {
        if (Status is ServerStatus.Online or ServerStatus.Starting) return;

        Status = ServerStatus.Starting;
        _modelsPath = modelsPath;
        ActiveModel = model;

        Log.LogInformation("[{Server}] Preparing to start...", Name);

        if (model is not null)
            await EnsureModelFilesAsync(model);

        // V-cache quantization step-up ladder. A quantized V cache requires Flash Attention, which
        // some models/backends can't use (e.g. hybrid linear-attention models on Metal). Start at the
        // requested V quant and step toward less compression (q4_0 → q8_0 → f16) until the server
        // boots; f16 needs no Flash Attention, so it always succeeds. K stays at its configured quant.
        int[] vLadder = new[] { 4, 8, 16 }.Where(q => q >= KvCacheQuantV).DefaultIfEmpty(16).ToArray();
        int bootedV = vLadder[^1];

        for (int i = 0; i < vLadder.Length; i++)
        {
            if (i > 0)
            {
                Log.LogWarning("[{Server}] Startup failed with V cache {Prev} — stepping up to {Next}...",
                    Name, KvQuantLabel(vLadder[i - 1]), KvQuantLabel(vLadder[i]));
                KillProcess();
                Status = ServerStatus.Starting;
            }

            try
            {
                Launch(model, vQuantOverride: vLadder[i]);
                await WaitUntilReadyAsync();
                bootedV = vLadder[i];
                break;                                          // booted on this rung
            }
            catch (Exception) when (i < vLadder.Length - 1)
            {
                // not the last rung — loop steps up to the next, less-compressed V quant
            }
        }

        Status = ServerStatus.Online;
        Log.LogInformation("[{Server}] Online (PID {Pid}, K{K}/V {V}).", Name, Pid, KvQuantLabel(KvCacheQuantK), KvQuantLabel(bootedV));
    }

    public void Stop()
    {
        Status = ServerStatus.Stopping;
        Log.LogInformation("[{Server}] Stopping...", Name);

        KillProcess();
        ActiveModel = null;
        Status = ServerStatus.Offline;
        Log.LogInformation("[{Server}] Stopped.", Name);
    }

    /// <summary>Terminate and dispose the llama-server process without touching server state
    /// (Status/ActiveModel) — used by Stop() and by the startup KV-fallback retry.</summary>
    private void KillProcess()
    {
        if (_process is not null && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }

        _process?.Dispose();
        _process = null;
        Pid = -1;
    }

    public async Task RestartAsync()
    {
        Model? model = ActiveModel;
        string modelPath = _modelsPath;
        Stop();
        await StartAsync(model, modelPath);
    }

    public async Task ChangeModelAsync(Model newModel, string modelsPath)
    {
        Log.LogInformation("[{Server}] Changing model to {Model}...", Name, newModel.Name);
        await WaitForIdleAsync();
        Stop();
        await StartAsync(newModel, modelsPath);
    }

    public void Dispose() => Stop();

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

    // ── Model file download ────────────────────────────────────────────────────

    private async Task EnsureModelFilesAsync(Model model)
    {
        await EnsureFileAsync(model.DownloadLink, model.Path);

        if (VisionEnabled && !string.IsNullOrWhiteSpace(model.MmprojDownloadLink))
            await EnsureFileAsync(model.MmprojDownloadLink, model.MmprojPath);
    }

    private async Task EnsureFileAsync(string url, string filename)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        string dest = Path.Combine(_modelsPath, filename);
        if (File.Exists(dest))
        {
            Log.LogInformation("[{Server}] Model file exists: {File}", Name, filename);
            return;
        }

        Log.LogInformation("[{Server}] Downloading {File}...", Name, filename);
        Directory.CreateDirectory(_modelsPath);

        using HttpClient hc = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        hc.DefaultRequestHeaders.Add("User-Agent", "ARI/1.0");

        using HttpResponseMessage resp = await hc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        string temp = dest + ".tmp";
        int lastPct = -1;
        long downloaded = 0;

        try
        {
            await using Stream src = await resp.Content.ReadAsStreamAsync();
            await using Stream dst = File.Create(temp);
            byte[] buf = new byte[81920];
            int read;

            while ((read = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read));
                downloaded += read;

                if (total > 0)
                {
                    int pct = (int)(downloaded * 100 / total.Value);
                    if (pct != lastPct && pct % 5 == 0)
                    {
                        Log.LogInformation("[{Server}] {File}: {Pct}% ({MB:F0} MB)", Name, filename, pct, downloaded / 1_048_576.0);
                        lastPct = pct;
                    }
                }
            }

            File.Move(temp, dest, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }

        Log.LogInformation("[{Server}] Download complete: {File}", Name, filename);
    }

    // ── Process management ────────────────────────────────────────────────────

    private void Launch(Model? model, int? vQuantOverride = null)
    {
        KillPortOwner(Port);
        string kvQuantK = KvQuantLabel(KvCacheQuantK);
        // V quant comes from the startup step-up ladder (see StartAsync): a quantized V cache needs
        // Flash Attention, so when a model can't use FA the ladder steps to a less-compressed V until
        // it boots. vQuantOverride is the rung being attempted; null uses the configured value.
        string kvQuantV = KvQuantLabel(vQuantOverride ?? KvCacheQuantV);

        float temp = model?.Temp ?? 0.6f;
        float topP = model?.TopP ?? 0.95f;
        int topK = model?.TopK ?? 40;
        float minP = model?.MinP ?? 0.0f;
        float repeatPenalty = model?.RepeatPenalty ?? 1.0f;
        bool jinja = model?.Jinja ?? true;
        bool mtp = model?.MTP ?? false;

        List<string> args = new()
        {
            model is not null ? $"-m \"{System.IO.Path.Combine(_modelsPath, model.Path)}\"" : "",
            VisionEnabled && model?.MmprojPath is { Length: > 0 }
                ? $"--mmproj \"{System.IO.Path.Combine(_modelsPath, model.MmprojPath)}\""
                : "",
            mtp ? "--spec-type draft-mtp --spec-draft-n-max 3" : "",
            "--flash-attn on",
            // Larger physical/logical batch speeds prompt-processing (the dominant cost with a long system
            // prompt on a slow dense model). Flash-attention above keeps the extra batch's memory in check.
            "-b 4096 -ub 1024",
            $"--cache-type-k {kvQuantK} --cache-type-v {kvQuantV}",
            $"-c {ContextSize}",
            "--n-predict -1",
            $"--temp {temp:F2} --top-p {topP:F2} --top-k {topK} --min-p {minP:F2} --repeat-penalty {repeatPenalty:F2}",
            jinja ? "--jinja" : "",
            $"-np {ParallelSlots} -ngl 99 --port {Port}",
            "--host 127.0.0.1",
            UnifiedCache ? "--kv-unified --cache-reuse 256" : "",
        };

        args.RemoveAll(string.IsNullOrWhiteSpace);

        _process = Process.Start(new ProcessStartInfo("llama-server", string.Join(" ", args))
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        }) ?? throw new Exception($"[{Name}] Failed to start llama-server process.");

        Pid = _process.Id;
        Log.LogInformation("[{Server}] llama-server started (PID {Pid}).", Name, Pid);
    }

    private async Task WaitUntilReadyAsync()
    {
        Log.LogInformation("[{Server}] Waiting for llama-server to come online...", Name);

        using HttpClient hc = new();
        DateTime timeout = DateTime.UtcNow.AddMinutes(3);

        while (DateTime.UtcNow < timeout)
        {
            if (_process?.HasExited == true)
                throw new Exception($"[{Name}] llama-server exited unexpectedly during startup.");

            try
            {
                HttpResponseMessage resp = await hc.GetAsync($"{FullEndpoint}/health");
                if (resp.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }

            await Task.Delay(1000);
        }

        throw new Exception($"[{Name}] llama-server did not come online within 3 minutes.");
    }

    private async Task WaitForIdleAsync(int timeoutSeconds = 60)
    {
        using HttpClient hc = new();
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                HttpResponseMessage resp = await hc.GetAsync($"{FullEndpoint}/slots");
                if (resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
                    bool allIdle = true;
                    foreach (System.Text.Json.JsonElement slot in doc.RootElement.EnumerateArray())
                        if (slot.GetProperty("state").GetInt32() != 0) { allIdle = false; break; }
                    if (allIdle) return;
                }
            }
            catch { return; }

            await Task.Delay(500);
        }

        Log.LogWarning("[{Server}] Timed out waiting for idle — forcing shutdown.", Name);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string KvQuantLabel(int bits) => bits switch
    {
        4 => "q4_0",
        8 => "q8_0",
        _ => "f16",
    };
}

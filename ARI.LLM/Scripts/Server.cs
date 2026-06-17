using ARI.Common;
using System.Diagnostics;
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

    // ── Runtime state (not persisted) ───────────────────────────────────────────

    [JsonIgnore] public ServerStatus Status { get; private set; } = ServerStatus.Offline;
    [JsonIgnore] public int Pid { get; private set; } = -1;
    [JsonIgnore] public Model? ActiveModel { get; private set; }

    [JsonIgnore] public string FullEndpoint => $"{Endpoint}:{Port}";

    // ── Internals ────────────────────────────────────────────────────────────────

    private Process? _process;
    private string _modelsPath = "";

    // ── Public lifecycle ─────────────────────────────────────────────────────────

    public async Task StartAsync(Model? model, string modelsPath)
    {
        if (Status is ServerStatus.Online or ServerStatus.Starting) return;

        Status = ServerStatus.Starting;
        _modelsPath = modelsPath;
        ActiveModel = model;

        Shared.Logger.LogInformation("[{Server}] Preparing to start...", Name);

        if (model is not null)
            await EnsureModelFilesAsync(model);

        Launch(model);
        await WaitUntilReadyAsync();

        Status = ServerStatus.Online;
        Shared.Logger.LogInformation("[{Server}] Online (PID {Pid}).", Name, Pid);
    }

    public void Stop()
    {
        Status = ServerStatus.Stopping;
        Shared.Logger.LogInformation("[{Server}] Stopping...", Name);

        if (_process is not null && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }

        _process?.Dispose();
        _process = null;
        Pid = -1;
        ActiveModel = null;
        Status = ServerStatus.Offline;
        Shared.Logger.LogInformation("[{Server}] Stopped.", Name);
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
        Shared.Logger.LogInformation("[{Server}] Changing model to {Model}...", Name, newModel.Name);
        await WaitForIdleAsync();
        Stop();
        await StartAsync(newModel, modelsPath);
    }

    public void Dispose() => Stop();

    // ── Model file download ────────────────────────────────────────────────────

    private async Task EnsureModelFilesAsync(Model model)
    {
        await EnsureFileAsync(model.DownloadLink, model.Path);

        if (!string.IsNullOrWhiteSpace(model.MmprojDownloadLink))
            await EnsureFileAsync(model.MmprojDownloadLink, model.MmprojPath);
    }

    private async Task EnsureFileAsync(string url, string filename)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        string dest = Path.Combine(_modelsPath, filename);
        if (File.Exists(dest))
        {
            Shared.Logger.LogInformation("[{Server}] Model file exists: {File}", Name, filename);
            return;
        }

        Shared.Logger.LogInformation("[{Server}] Downloading {File}...", Name, filename);
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
                        Shared.Logger.LogInformation("[{Server}] {File}: {Pct}% ({MB:F0} MB)", Name, filename, pct, downloaded / 1_048_576.0);
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

        Shared.Logger.LogInformation("[{Server}] Download complete: {File}", Name, filename);
    }

    // ── Process management ────────────────────────────────────────────────────

    private void Launch(Model? model)
    {
        string kvQuantK = KvQuantLabel(KvCacheQuantK);
        string kvQuantV = KvQuantLabel(KvCacheQuantV);

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
            model?.MmprojDownloadLink is { Length: > 0 }
                ? $"--mmproj \"{System.IO.Path.Combine(_modelsPath, model.MmprojPath)}\""
                : "",
            mtp ? "--spec-type draft-mtp --spec-draft-n-max 3" : "",
            $"--cache-type-k {kvQuantK} --cache-type-v {kvQuantV}",
            $"-c {ContextSize}",
            "--n-predict -1",
            $"--temp {temp:F2} --top-p {topP:F2} --top-k {topK} --min-p {minP:F2} --repeat-penalty {repeatPenalty:F2}",
            jinja ? "--jinja" : "",
            $"-np {ParallelSlots} -ngl 99 --port {Port}",
            "--host 127.0.0.1",
        };

        args.RemoveAll(string.IsNullOrWhiteSpace);

        _process = Process.Start(new ProcessStartInfo("llama-server", string.Join(" ", args))
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        }) ?? throw new Exception($"[{Name}] Failed to start llama-server process.");

        Pid = _process.Id;
        Shared.Logger.LogInformation("[{Server}] llama-server started (PID {Pid}).", Name, Pid);
    }

    private async Task WaitUntilReadyAsync()
    {
        Shared.Logger.LogInformation("[{Server}] Waiting for llama-server to come online...", Name);

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

        Shared.Logger.LogWarning("[{Server}] Timed out waiting for idle — forcing shutdown.", Name);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string KvQuantLabel(int bits) => bits switch
    {
        4 => "q4_0",
        8 => "q8_0",
        _ => "f16",
    };
}

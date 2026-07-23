using ARI.Common;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public enum ServerStatus { Offline, Starting, Online, Stopping }

/// <summary>A named parallel slot on a server. Agents are assigned to slots (not servers directly) —
/// the slot's ContextLimit is the ONLY source of context-window truth an agent has (Agent.BudgetContext
/// derives from this); an agent's own config never states its own context size. The slot's position in
/// Server.Slots is its numeric id_slot, resolved at bind time — renaming/reordering is safe, deleting
/// one out from under a bound agent is not (the agent falls back to unbound-slot behaviour).</summary>
public class NamedSlot
{
    [JsonPropertyName("id")]           public Guid   Id           { get; init; } = Guid.NewGuid();
    [JsonPropertyName("name")]         public string Name         { get; set; } = "";
    [JsonPropertyName("contextLimit")] public int    ContextLimit { get; set; } = 8192;
}

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

    // Named, user-managed slots (create/rename/delete in the control panel). The list IS the source of
    // truth for how many parallel streams this server runs — -np is derived from its length, never set
    // independently. A fresh server seeds one "Default" slot covering the full context.
    [JsonPropertyName("slots")]
    public List<NamedSlot> Slots { get; set; } = new() { new NamedSlot { Name = "Default", ContextLimit = 32768 } };

    [JsonIgnore]
    public int ParallelSlots => Math.Max(1, Slots.Count);

    /// <summary>Sum of every slot's ContextLimit — may legitimately exceed ContextSize (not all slots
    /// peak at once under kv-unified's shared pool), but the UI should warn when it does.</summary>
    [JsonIgnore]
    public int TotalAllocatedContext => Slots.Sum(s => s.ContextLimit);

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

    // ── Sampler defaults (the catch-all every agent falls back to) ────────────────
    // Non-nullable — a server's sampler settings are never "blank"; they seed from llama.cpp's own
    // binary defaults on creation (see llama-server --help) and are always concrete from then on. An
    // agent may override any of these individually via its own nullable fields, gated by
    // OverrideSamplerSettings; when that's off (or a given field is left blank even with it on), the
    // agent falls through to whatever's here. This is the ONLY place that fallback bottoms out —
    // there is no further fallback to "let llama.cpp decide" by omitting the param.

    [JsonPropertyName("temperature")]      public double Temperature      { get; set; } = 0.80;
    [JsonPropertyName("topP")]             public double TopP             { get; set; } = 0.95;
    [JsonPropertyName("topK")]             public int    TopK             { get; set; } = 40;
    [JsonPropertyName("minP")]             public double MinP             { get; set; } = 0.05;
    [JsonPropertyName("topNSigma")]        public double TopNSigma        { get; set; } = -1.00;
    [JsonPropertyName("typicalP")]         public double TypicalP         { get; set; } = 1.00;
    [JsonPropertyName("xtcProbability")]   public double XtcProbability   { get; set; } = 0.00;
    [JsonPropertyName("xtcThreshold")]     public double XtcThreshold     { get; set; } = 0.10;
    [JsonPropertyName("dynatempRange")]    public double DynatempRange    { get; set; } = 0.00;
    [JsonPropertyName("dynatempExp")]      public double DynatempExp      { get; set; } = 1.00;
    [JsonPropertyName("repeatLastN")]      public int    RepeatLastN      { get; set; } = 64;
    [JsonPropertyName("repeatPenalty")]    public double RepeatPenalty    { get; set; } = 1.00;
    [JsonPropertyName("presencePenalty")]  public double PresencePenalty  { get; set; } = 0.00;
    [JsonPropertyName("frequencyPenalty")] public double FrequencyPenalty { get; set; } = 0.00;
    [JsonPropertyName("dryMultiplier")]    public double DryMultiplier    { get; set; } = 0.00;
    [JsonPropertyName("dryBase")]          public double DryBase          { get; set; } = 1.75;
    [JsonPropertyName("dryAllowedLength")] public int    DryAllowedLength { get; set; } = 2;
    [JsonPropertyName("dryPenaltyLastN")]  public int    DryPenaltyLastN  { get; set; } = -1;
    [JsonPropertyName("drySequenceBreakers")] public string[] DrySequenceBreakers { get; set; } = new[] { "\n", ":", "\"", "*" };
    [JsonPropertyName("mirostat")]         public int    Mirostat         { get; set; } = 0;
    [JsonPropertyName("mirostatLr")]       public double MirostatLr       { get; set; } = 0.10;
    [JsonPropertyName("mirostatEnt")]      public double MirostatEnt      { get; set; } = 5.00;
    [JsonPropertyName("seed")]             public long   Seed             { get; set; } = -1;

    // ── Runtime state (not persisted) ───────────────────────────────────────────

    [JsonIgnore] public ServerStatus Status { get; private set; } = ServerStatus.Offline;
    [JsonIgnore] public int Pid { get; private set; } = -1;
    [JsonIgnore] public Model? ActiveModel { get; private set; }

    [JsonIgnore] public string FullEndpoint => $"{Endpoint}:{Port}";

    // ── Internals ────────────────────────────────────────────────────────────────

    private ILogger? _logger;
    private Process? _process;
    private StreamWriter? _llamaLog;   // llama-server's own output — kept out of ARI's console/log
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

        _llamaLog?.Dispose();
        _llamaLog = null;
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

        bool jinja = model?.Jinja ?? true;
        bool mtp = model?.MTP ?? false;
        // llama-server deprecated repeating --dry-sequence-breaker (comma-separated now); the default
        // set itself contains raw '"' and '\n', which mangle a shell-joined argument string either way.
        // Cheapest correct fix: omit the flag entirely for the default set (llama.cpp's own built-in
        // default is identical, so behaviour is unchanged) — only emit it, comma-joined and escaped,
        // when actually customised away from the default.
        string[] defaultBreakers = { "\n", ":", "\"", "*" };
        string breakers =
            DrySequenceBreakers.Length == 0 ? "--dry-sequence-breaker none" :
            DrySequenceBreakers.SequenceEqual(defaultBreakers) ? "" :
            $"--dry-sequence-breaker \"{string.Join(",", DrySequenceBreakers.Select(b => b.Replace("\\", "\\\\").Replace("\"", "\\\"")))}\"";

        List<string> args = new()
        {
            model is not null ? $"-m \"{System.IO.Path.Combine(_modelsPath, model.Path)}\"" : "",
            VisionEnabled && model?.MmprojPath is { Length: > 0 }
                ? $"--mmproj \"{System.IO.Path.Combine(_modelsPath, model.MmprojPath)}\""
                : "",
            mtp ? "--spec-type draft-mtp --spec-draft-n-max 3" : "",
            "--flash-attn on",
            // Larger physical/logical batch speeds prompt-processing — the dominant cost of the coding
            // pipeline (one sweep over the MoE expert weights per ub prompt tokens). ub is capped at 2048:
            // 4096 OOMs the Metal backend MID-PREFILL (kIOGPUCommandBufferCallbackErrorOutOfMemory) even
            // with the KV pool at 160k, and a mid-prefill OOM wedges llama-server until it is restarted —
            // the startup V-quant ladder cannot catch it because boot succeeds.
            "-b 4096 -ub 2048",
            $"--cache-type-k {kvQuantK} --cache-type-v {kvQuantV}",
            $"-c {ContextSize}",
            "--n-predict -1",
            // Sampler defaults for this server — the catch-all every agent falls back to when it has no
            // override (or OverrideSamplerSettings is off). Always concrete, never omitted.
            $"--temp {Temperature:F2} --top-p {TopP:F2} --top-k {TopK} --min-p {MinP:F2}",
            $"--top-n-sigma {TopNSigma:F2} --typical-p {TypicalP:F2}",
            $"--xtc-probability {XtcProbability:F2} --xtc-threshold {XtcThreshold:F2}",
            $"--dynatemp-range {DynatempRange:F2} --dynatemp-exp {DynatempExp:F2}",
            $"--repeat-last-n {RepeatLastN} --repeat-penalty {RepeatPenalty:F2}",
            $"--presence-penalty {PresencePenalty:F2} --frequency-penalty {FrequencyPenalty:F2}",
            $"--dry-multiplier {DryMultiplier:F2} --dry-base {DryBase:F2} --dry-allowed-length {DryAllowedLength} --dry-penalty-last-n {DryPenaltyLastN}",
            breakers,
            $"--mirostat {Mirostat} --mirostat-lr {MirostatLr:F2} --mirostat-ent {MirostatEnt:F2}",
            $"--seed {Seed}",
            jinja ? "--jinja" : "",
            // Reasoning: separate the chain-of-thought into message.reasoning_content, and — since thinking
            // is budgeted PER-REQUEST via `thinking_budget_tokens` — leave the server budget unset (never pass
            // --reasoning-budget, which would disable per-request overrides). The budget-message is injected
            // before the forced end-of-thinking so a budgeted turn wraps up its thought instead of being chopped.
            "--reasoning-format deepseek",
            "--reasoning-budget-message \"I've used most of my thinking budget. Let me finish this thought, state my conclusion in one line, and act on it now.\"",
            $"-np {ParallelSlots} -ngl 99 --port {Port}",
            "--host 127.0.0.1",
            // Last-resort safety net for a request that overflows its slot's context despite compaction
            // (e.g. a non-compacting agent) — costs nothing until it actually triggers (see design notes).
            "--context-shift",
            UnifiedCache ? "--kv-unified --cache-reuse 256" : "",
        };

        args.RemoveAll(string.IsNullOrWhiteSpace);

        // Redirect llama-server's (very verbose) output to its own file instead of letting it inherit
        // ARI's stdout — otherwise it floods the console window and any captured log. Draining the
        // pipes is also required, or llama-server blocks once the OS buffer fills.
        _process = Process.Start(new ProcessStartInfo(Shared.LlamaServer, string.Join(" ", args))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new Exception($"[{Name}] Failed to start llama-server process.");

        Directory.CreateDirectory(Paths.Logs);
        _llamaLog = new StreamWriter(Path.Combine(Paths.Logs, $"llama-{Name}.log"), append: false) { AutoFlush = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_llamaLog) _llamaLog.WriteLine(e.Data); };
        _process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (_llamaLog) _llamaLog.WriteLine(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

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

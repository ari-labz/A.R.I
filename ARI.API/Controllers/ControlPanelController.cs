using ARI.API.Data;
using ARI.Common;
using ARI.LLM;
using ARI.Voice;
using ARI.VoiceSynthesis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System.Text.Json;

namespace ARI.API.Controllers;

public class ControlPanelController : Controller
{
    // Control panel UI is served as a static file at /controlpanel.html (from ARI.UI/public/).
    // This Razor route is unused — redirect so nothing breaks if someone hits /ControlPanel.
    public IActionResult Index() => Redirect("/controlpanel.html");
}

[Route("api/cp")]
[ApiController]
public class ControlPanelApiController(APIConfig config, SystemInfo systemInfo, PersistentData persistentData) : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    /// <summary>
    /// SSE stream of the ARI.log tail — sends the last 100 lines on connect,
    /// then streams new lines as they are appended.
    /// </summary>
    [HttpGet("log")]
    public async Task StreamLog(CancellationToken cancellationToken)
    {
        Response.Headers[HeaderNames.ContentType]  = "text/event-stream";
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        Response.Headers["X-Accel-Buffering"]      = "no";

        string logPath = Shared.LogPath;
        if (string.IsNullOrEmpty(logPath) || !System.IO.File.Exists(logPath))
        {
            await Response.WriteAsync("data: (log file not found)\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        // Send tail of existing content first
        string[] initial = ReadTail(logPath, 100);
        foreach (string line in initial)
            await Response.WriteAsync($"data: {EscapeSse(line)}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);

        // Watch for new lines
        long position = new System.IO.FileInfo(logPath).Length;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(500, cancellationToken);
            long newLength = new System.IO.FileInfo(logPath).Length;
            if (newLength <= position) continue;

            using System.IO.FileStream fs = new(logPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            fs.Seek(position, System.IO.SeekOrigin.Begin);
            using System.IO.StreamReader reader = new(fs);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                await Response.WriteAsync($"data: {EscapeSse(line)}\n\n", cancellationToken);
            position = newLength;
            await Response.Body.FlushAsync(cancellationToken);
        }
    }

    [HttpGet("conventions")]
    public IActionResult GetConventions() => Ok(new { text = ConventionsStore.Get() });

    [HttpPost("conventions")]
    public IActionResult SetConventions([FromBody] ConventionsRequest req)
    {
        ConventionsStore.Set(req.Text ?? "");
        return Ok(new { ok = true });
    }

    [HttpGet("ram")]
    public IActionResult GetRam()
    {
        long bytes = systemInfo.GetTotalRamBytes();

        var liveCalls = Llm?.LiveCalls().Select(c => new
        {
            agentName             = c.AgentName,
            threadKey             = c.ThreadKey,
            estimatedOutputTokens = c.EstimatedOutputTokens,
            outputTokenLimit      = c.OutputTokenLimit,
            estimatedInputTokens  = c.EstimatedInputTokens,
            contextTokenLimit     = c.ContextTokenLimit,
            imageTokenLimit       = c.ImageTokenLimit,
            hadImages             = c.HadImages,
            outputPct             = c.OutputTokenLimit > 0
                                     ? (int)(c.EstimatedOutputTokens * 100.0 / c.OutputTokenLimit)
                                     : 0,
        }).ToList() ?? new();

        List<object> context = new();
        if (Llm is not null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Agent? codeAgent = Llm.Agents.GetValueOrDefault("Code");
            if (codeAgent is not null)
                foreach (KeyValuePair<string, ARI.LLM.Thread> kvp in codeAgent.Threads)
                {
                    (int used, int limit) = codeAgent.GetContextStats(kvp.Value);
                    if (used <= 0) continue;
                    seen.Add(kvp.Key);
                    context.Add(new { threadKey = kvp.Key, agentName = "Code", used, limit, pct = limit > 0 ? (int)(used * 100.0 / limit) : 0 });
                }
            Agent? dialogueAgent = Llm.Agents.GetValueOrDefault("Dialogue");
            if (dialogueAgent is not null)
                foreach (KeyValuePair<string, ARI.LLM.Thread> kvp in dialogueAgent.Threads)
                {
                    if (seen.Contains(kvp.Key)) continue;
                    (int used, int limit) = dialogueAgent.GetContextStats(kvp.Value);
                    if (used <= 0) continue;
                    context.Add(new { threadKey = kvp.Key, agentName = "Dialogue", used, limit, pct = limit > 0 ? (int)(used * 100.0 / limit) : 0 });
                }
        }

        var breakdown = systemInfo.GetRamBreakdown()
            .Select(s => new { label = s.Label, serverName = s.ServerName, mb = Math.Round(s.Bytes / 1024.0 / 1024.0, 1) })
            .ToList();

        double swapMb = systemInfo.GetSwapMb();
        return Ok(new { ramBytes = bytes, ramMb = bytes / 1024.0 / 1024.0, swapMb, liveCalls, context, breakdown });
    }

    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        long ramBytes = systemInfo.GetTotalRamBytes();

        List<object> callStats = new();
        if (Llm is not null)
        {
            foreach (LLMModule.LlmCallStat c in Llm.CallStats())
            {
                // Emit Vision row before the agent row when image tokens were spent
                if (c.HadImageAttachments && c.EstimatedImageTokens > 0)
                {
                    int imgPct = c.ImageTokenLimit > 0
                        ? (int)(c.EstimatedImageTokens * 100.0 / c.ImageTokenLimit)
                        : 0;
                    callStats.Add(new
                    {
                        agentName            = "Vision",
                        threadKey            = c.ThreadKey,
                        timestamp            = c.Timestamp,
                        completionTokens     = 0,
                        outputTokenLimit     = 0,
                        outputPct            = 0,
                        promptTokens         = c.EstimatedImageTokens,
                        contextTokenLimit    = 0,
                        inputPct             = 0,
                        hadImages            = true,
                        estimatedImageTokens = c.EstimatedImageTokens,
                        imageTokenLimit      = c.ImageTokenLimit,
                        imagePct             = imgPct,
                    });
                }

                callStats.Add(new
                {
                    agentName            = c.AgentName,
                    threadKey            = c.ThreadKey,
                    timestamp            = c.Timestamp,
                    completionTokens     = c.CompletionTokens,
                    outputTokenLimit     = c.OutputTokenLimit,
                    outputPct            = c.OutputTokenLimit > 0
                                            ? (int)(c.CompletionTokens * 100.0 / c.OutputTokenLimit)
                                            : 0,
                    promptTokens         = c.PromptTokens,
                    contextTokenLimit    = c.ContextTokenLimit,
                    inputPct             = c.ContextTokenLimit > 0
                                            ? (int)(c.PromptTokens * 100.0 / c.ContextTokenLimit)
                                            : 0,
                    hadImages            = c.HadImageAttachments,
                    estimatedImageTokens = c.EstimatedImageTokens,
                });
            }
        }

        List<object> contextStats = new();
        if (Llm is not null)
        {
            // Collect all user-facing threads across Dialogue and Code, deduped by key.
            // For threads present in both agents, prefer the Code label.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Agent? codeAgent = Llm.Agents.GetValueOrDefault("Code");
            if (codeAgent is not null)
            {
                foreach (KeyValuePair<string, ARI.LLM.Thread> kvp in codeAgent.Threads)
                {
                    (int used, int limit) = codeAgent.GetContextStats(kvp.Value);
                    if (used <= 0) continue;
                    seen.Add(kvp.Key);
                    contextStats.Add(new
                    {
                        threadKey = kvp.Key,
                        agentName = "Code",
                        used,
                        limit,
                        pct = limit > 0 ? (int)(used * 100.0 / limit) : 0,
                    });
                }
            }

            Agent? dialogueAgent = Llm.Agents.GetValueOrDefault("Dialogue");
            if (dialogueAgent is not null)
            {
                foreach (KeyValuePair<string, ARI.LLM.Thread> kvp in dialogueAgent.Threads)
                {
                    if (seen.Contains(kvp.Key)) continue;  // already listed under Code
                    (int used, int limit) = dialogueAgent.GetContextStats(kvp.Value);
                    if (used <= 0) continue;
                    contextStats.Add(new
                    {
                        threadKey = kvp.Key,
                        agentName = "Dialogue",
                        used,
                        limit,
                        pct = limit > 0 ? (int)(used * 100.0 / limit) : 0,
                    });
                }
            }
        }

        return Ok(new
        {
            ramBytes,
            ramMb     = ramBytes / 1024.0 / 1024.0,
            calls     = callStats,
            context   = contextStats,
        });
    }

    private static string[] ReadTail(string path, int lineCount)
    {
        using System.IO.FileStream fs = new(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
        using System.IO.StreamReader reader = new(fs);
        Queue<string> queue = new();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            queue.Enqueue(line);
            if (queue.Count > lineCount) queue.Dequeue();
        }
        return queue.ToArray();
    }

    private static string EscapeSse(string s) => s.Replace("\n", "↵").Replace("\r", "");

    // ── Agent assignment ─────────────────────────────────────────────────────────

    [HttpGet("agents")]
    public IActionResult GetAgents()
    {
        var agents = persistentData.GetAgents();
        return Ok(new { agents });
    }

    [HttpPut("agents/{name}")]
    public IActionResult UpdateAgent(string name, [FromBody] AgentDefinition req)
    {
        req.Name = name;

        if (!persistentData.UpdateAgent(req))
            return NotFound(new { error = $"Agent '{name}' not found." });

        // Apply server/slot changes live if LLM is running
        if (Llm is not null)
        {
            Llm.AssignAgentServer(name, req.ServerName);
            if (req.Slot.HasValue) Llm.AssignAgentSlot(name, req.Slot.Value);
        }

        return NoContent();
    }
}

// ── Voice Synthesis API ───────────────────────────────────────────────────────

[Route("api/cp/voice")]
[ApiController]
public class VoiceController(
    VoiceSynthesisConfig vsConfig,
    ILoggerFactory loggerFactory,
    IHostApplicationLifetime lifetime) : ControllerBase
{
    private readonly ILogger logger = loggerFactory.CreateLogger("ARI.WebPanel");
    private VoiceSynthesisModule? voiceTraining => (VoiceSynthesisModule?)Modules.VoiceSynthesis;
    private VoiceModule?          voiceService  => (VoiceModule?)Modules.Voice;
    private LLMModule?            llm           => (LLMModule?)Modules.Llm;
    private static readonly string StagingRoot = Path.Combine(Path.GetTempPath(), "ari-voice-staging");
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> chunkCounters = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> assembleLocks = new();

    [HttpPost("stage")]
    public IActionResult CreateStage()
    {
        string stageId  = Guid.NewGuid().ToString("N");
        string stageDir = Path.Combine(StagingRoot, stageId);
        Directory.CreateDirectory(stageDir);
        return Ok(new { stageId, stagingPath = stageDir });
    }

    [HttpPost("upload")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> Upload(
        [FromQuery] string stageId,
        [FromQuery] string name,
        [FromQuery] int chunk       = 0,
        [FromQuery] int totalChunks = 1)
    {
        try
        {
        if (string.IsNullOrWhiteSpace(stageId) || string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "stageId and name are required." });

        string stageDir = Path.Combine(StagingRoot, stageId);
        if (!Directory.Exists(stageDir))
            return BadRequest(new { error = "Unknown stageId. Call /stage first." });

        string safeName  = Path.GetFileName(name);
        string chunkPath = Path.Combine(stageDir, $"{safeName}.part{chunk}");

        // Write chunk fully before incrementing the counter — guarantees file is closed when count is read
        await using (System.IO.FileStream fs = System.IO.File.Create(chunkPath))
            await Request.Body.CopyToAsync(fs);

        string counterKey    = $"{stageId}:{safeName}";
        int    completedCount = chunkCounters.AddOrUpdate(counterKey, 1, (_, existing) => existing + 1);
        logger.LogInformation("[Voice] Chunk {Chunk}/{Total} written for {Name} ({Done} done)", chunk + 1, totalChunks, safeName, completedCount);

        // Only assemble once all chunks are fully written — atomic counter guarantees no partial files
        if (completedCount == totalChunks)
        {
            string assembleKey = $"{stageId}:{safeName}:assemble";
            SemaphoreSlim gate = assembleLocks.GetOrAdd(assembleKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                string dest = Path.Combine(stageDir, safeName);
                if (!System.IO.File.Exists(dest))
                {
                    await using (System.IO.FileStream outFs = System.IO.File.Create(dest))
                    {
                        for (int i = 0; i < totalChunks; i++)
                        {
                            string part = Path.Combine(stageDir, $"{safeName}.part{i}");
                            await using System.IO.FileStream partFs = System.IO.File.OpenRead(part);
                            await partFs.CopyToAsync(outFs);
                        }
                    }
                    for (int i = 0; i < totalChunks; i++)
                        System.IO.File.Delete(Path.Combine(stageDir, $"{safeName}.part{i}"));
                    chunkCounters.TryRemove(counterKey, out _);
                    logger.LogInformation("[Voice] Assembled {Name} ({Chunks} chunks) → {Dir}", safeName, totalChunks, stageDir);
                }
            }
            finally
            {
                gate.Release();
                assembleLocks.TryRemove(assembleKey, out _);
            }
        }

        return Ok(new { stagingPath = stageDir, chunk, totalChunks });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Voice] Upload failed for chunk {Chunk} of {Name}", chunk, name);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>Start a training job from a previously uploaded staging path.</summary>
    [HttpPost("train")]
    public IActionResult StartTraining([FromBody] TrainRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
            return BadRequest(new { error = "modelName is required." });
        if (string.IsNullOrWhiteSpace(req.StagingPath) || !Directory.Exists(req.StagingPath))
            return BadRequest(new { error = "stagingPath does not exist." });
        if (string.IsNullOrEmpty(vsConfig.StyleTtsPath) || string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis module is not configured." });
        if (voiceTraining?.IsSetupComplete != true)
            return StatusCode(503, new { error = "StyleTTS2 is still installing. Please wait." });

        TrainingJob job;
        try
        {
            StyleTtsTrainer trainer = new(
                styleTtsPath:    vsConfig.StyleTtsPath,
                voicesPath:      vsConfig.VoicesPath,
                audioPath:       req.StagingPath,
                modelName:       req.ModelName,
                epochs:          req.Epochs,
                saveEveryNEpochs: req.SaveEveryNEpochs,
                logger:          logger);

            job = voiceTraining!.Start(trainer, req.ModelName, lifetime.ApplicationStopping);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        logger.LogInformation(
            "[Voice] Training started — model: {ModelName}, epochs: {Epochs}",
            req.ModelName, req.Epochs);

        // Stop llama servers to free RAM, run training, then restart them
        string stagingPath = req.StagingPath;
        string modelName   = req.ModelName;
        _ = Task.Run(async () =>
        {
            if (llm is not null) await llm.StopAllServersAsync();

            while (job.IsRunning)
                await Task.Delay(2000);
            try { Directory.Delete(stagingPath, recursive: true); }
            catch { /* best-effort */ }

            if (job.IsSuccess)
            {
                logger.LogInformation("[Voice] Voice Synthesis of {ModelName} complete", modelName);
                if (Modules.Discord is not null)
                    await Modules.Discord.NotifyOwner($"> Voice Synthesis of {modelName} complete");
            }
            else
            {
                logger.LogWarning("[Voice] Training failed for {ModelName}: {Error}", modelName, job.Error);
            }

            if (llm is not null) await llm.RestartAllServersAsync();
        });

        return Ok(new { jobId = job.JobId, modelName = job.ModelName });
    }

    /// <summary>SSE stream of training progress events.</summary>
    [HttpGet("progress")]
    public async Task StreamProgress(CancellationToken ct)
    {
        Response.Headers[HeaderNames.ContentType]  = "text/event-stream";
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        Response.Headers["X-Accel-Buffering"]      = "no";

        var job = voiceTraining?.Current;
        if (job is null)
        {
            await Response.WriteAsync("data: {\"step\":\"Idle\",\"percent\":0}\n\n", ct);
            await Response.Body.FlushAsync(ct);
            return;
        }

        int  sent          = 0;
        long lastKeepalive = Environment.TickCount64;

        while (!ct.IsCancellationRequested)
        {
            var events = job.Events;
            bool wrote = false;
            while (sent < events.Count)
            {
                var ev   = events[sent++];
                string j = System.Text.Json.JsonSerializer.Serialize(new
                {
                    step    = ev.Step,
                    percent = ev.Percent,
                    detail  = ev.Detail,
                    ts      = ev.Timestamp,
                    done    = !job.IsRunning,
                    success = job.IsSuccess,
                    error   = job.Error,
                });
                await Response.WriteAsync($"data: {j}\n\n", ct);
                wrote = true;
            }

            // Send an SSE comment every 30 s so Cloudflare Tunnel doesn't close the idle connection
            if (Environment.TickCount64 - lastKeepalive > 30_000)
            {
                await Response.WriteAsync(": keepalive\n\n", ct);
                lastKeepalive = Environment.TickCount64;
                wrote = true;
            }

            if (wrote) await Response.Body.FlushAsync(ct);

            if (!job.IsRunning) break;
            await Task.Delay(300, ct);
        }
    }

    /// <summary>Current job status (for polling fallback).</summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var job = voiceTraining?.Current;
        if (job is null)
            return Ok(new { idle = true });

        return Ok(new
        {
            jobId     = job.JobId,
            modelName = job.ModelName,
            isRunning = job.IsRunning,
            isSuccess = job.IsSuccess,
            error     = job.Error,
            events    = job.Events,
        });
    }

    [HttpPost("speak")]
    public async Task<IActionResult> Speak([FromBody] SpeakRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "text is required." });
        if (voiceService?.IsReady != true)
            return StatusCode(503, new { error = "Voice module is not running." });

        byte[] wav = await voiceService.Synthesise(req.Text, ct);
        logger.LogInformation("[Voice/Speak] '{Text}' → {Bytes} bytes", req.Text, wav.Length);
        return File(wav, "audio/wav");
    }

    [HttpPost("split-sentences")]
    public IActionResult SplitSentences([FromBody] SplitSentencesRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "text is required." });
        var sentences = ARI.Voice.SentenceSplitter.Split(req.Text);
        return Ok(new { sentences });
    }

    [HttpGet("active")]
    public IActionResult GetActive() =>
        Ok(new { model = voiceService?.ActiveModel, ready = voiceService?.IsReady ?? false });

    [HttpGet("models")]
    public IActionResult GetModels()
    {
        if (string.IsNullOrEmpty(vsConfig.VoicesPath) || !Directory.Exists(vsConfig.VoicesPath))
            return Ok(new { models = Array.Empty<string>() });

        string[] models = Directory.GetDirectories(vsConfig.VoicesPath)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .OrderBy(n => n)
            .ToArray()!;

        return Ok(new { models });
    }
}

// ── LLM Models & Servers API ──────────────────────────────────────────────────

[Route("api/cp/models")]
[ApiController]
public class ModelsApiController(PersistentData persistentData) : ControllerBase
{
    private LLMModule? llm => (LLMModule?)Modules.Llm;
    private string ModelsPath => llm?.ModelsPath ?? "";

    [HttpGet]
    public IActionResult GetModels()
    {
        var notes      = persistentData.GetAllNotes();
        string mPath   = ModelsPath;

        var activeModelNames  = llm?.Servers.Select(s => s.ActiveModel?.Name).Where(n => n is not null).ToHashSet() ?? [];
        var startupModelNames = persistentData.GetServers().Select(s => s.CurrentModelName).Where(n => n is not null).ToHashSet();

        var models = persistentData.GetModels().Select(m =>
        {
            m.RefreshDownloadedState(mPath);
            return new
            {
                name               = m.Name,
                downloadLink       = m.DownloadLink,
                mmprojDownloadLink = m.MmprojDownloadLink,
                downloaded         = m.Downloaded,
                modelSize          = m.ModelSize,
                moe                = m.MoE,
                mtp                = m.MTP,
                notes              = notes.TryGetValue(m.Name, out string? n) ? n : "",
                active             = activeModelNames.Contains(m.Name),
                isStartup          = startupModelNames.Contains(m.Name),
            };
        }).ToList();

        var servers = persistentData.GetServers().Select(s =>
        {
            ServerStatus status = llm?.Servers.FirstOrDefault(r => r.Id == s.Id)?.Status ?? ServerStatus.Offline;
            Model?    active = llm?.Servers.FirstOrDefault(r => r.Id == s.Id)?.ActiveModel;
            return new
            {
                id              = s.Id,
                name            = s.Name,
                status          = status.ToString(),
                activeModelName = active?.Name,
                endpoint        = s.FullEndpoint,
                port            = s.Port,
                contextSize     = s.ContextSize,
                parallelSlots   = s.ParallelSlots,
                kvCacheQuantK   = s.KvCacheQuantK,
                kvCacheQuantV   = s.KvCacheQuantV,

                currentModelName = s.CurrentModelName,
                autoStart       = s.BootStartup,
            };
        }).ToList();

        return Ok(new { models, servers });
    }

    // ── Model CRUD ────────────────────────────────────────────────────────────

    [HttpPost]
    public IActionResult AddModel([FromBody] Model model)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
            return BadRequest(new { error = "name is required." });

        persistentData.AddModel(model);
        model.RefreshDownloadedState(ModelsPath);
        return Ok(model);
    }

    [HttpPut("{id:guid}")]
    public IActionResult UpdateModel(Guid id, [FromBody] Model model)
    {
        if (!persistentData.UpdateModel(model))
            return NotFound(new { error = "Model not found." });

        model.RefreshDownloadedState(ModelsPath);
        return Ok(model);
    }

    [HttpDelete("{name}")]
    public IActionResult DeleteModel(string name)
    {
        if (!persistentData.RemoveModel(Uri.UnescapeDataString(name)))
            return NotFound(new { error = "Model not found." });
        return Ok(new { ok = true });
    }

    // ── Server CRUD ───────────────────────────────────────────────────────────

    [HttpPost("/api/cp/servers")]
    public IActionResult AddServer([FromBody] Server server)
    {
        if (string.IsNullOrWhiteSpace(server.Name))
            return BadRequest(new { error = "name is required." });

        persistentData.AddServer(server);
        return Ok(server);
    }

    [HttpPut("/api/cp/servers/{id:guid}")]
    public IActionResult UpdateServer(Guid id, [FromBody] Server server)
    {
        if (!persistentData.UpdateServer(server))
            return NotFound(new { error = "Server not found." });
        return Ok(server);
    }

    [HttpDelete("/api/cp/servers/{id:guid}")]
    public IActionResult DeleteServer(Guid id)
    {
        Server? live = llm?.Servers.FirstOrDefault(s => s.Id == id);
        live?.Stop();
        if (!persistentData.RemoveServer(id))
            return NotFound(new { error = "Server not found." });
        return Ok(new { ok = true });
    }

    // ── Server lifecycle ──────────────────────────────────────────────────────

    [HttpPost("/api/cp/servers/{id:guid}/start")]
    public IActionResult StartServer(Guid id)
    {
        Server? server = llm?.Servers.FirstOrDefault(s => s.Id == id);
        if (server is null) return NotFound(new { error = "Server not found or LLM module unavailable." });

        Model? model = server.CurrentModelName is not null
            ? persistentData.GetModel(server.CurrentModelName)
            : null;
        string modelsPath = llm!.ModelsPath;
        _ = Task.Run(() => server.StartAsync(model, modelsPath));
        return Ok(new { ok = true });
    }

    [HttpPost("/api/cp/servers/{id:guid}/stop")]
    public IActionResult StopServer(Guid id)
    {
        Server? server = llm?.Servers.FirstOrDefault(s => s.Id == id);
        if (server is null) return NotFound(new { error = "Server not found or LLM module unavailable." });
        server.Stop();
        return Ok(new { ok = true });
    }

    [HttpPost("/api/cp/servers/{id:guid}/restart")]
    public IActionResult RestartServer(Guid id)
    {
        Server? server = llm?.Servers.FirstOrDefault(s => s.Id == id);
        if (server is null) return NotFound(new { error = "Server not found or LLM module unavailable." });
        _ = Task.Run(() => server.RestartAsync());
        return Ok(new { ok = true });
    }

    // ── Model switching & notes ───────────────────────────────────────────────

    [HttpPost("switch")]
    public IActionResult Switch([FromBody] SwitchModelRequest req)
    {
        if (req.ServerId == Guid.Empty)
            return BadRequest(new { error = "serverId is required." });
        if (string.IsNullOrWhiteSpace(req.ModelName))
            return BadRequest(new { error = "modelName is required." });

        Server? server = llm?.Servers.FirstOrDefault(s => s.Id == req.ServerId);
        if (server is null)
            return NotFound(new { error = "Server not found." });

        Model? model = persistentData.GetModel(req.ModelName);
        if (model is null)
            return NotFound(new { error = "Model not found." });

        string modelsPath = llm!.ModelsPath;
        _ = Task.Run(async () =>
        {
            await server.ChangeModelAsync(model, modelsPath);
            persistentData.SetServerCurrentModel(server.Id, model.Name);
        });

        return Ok(new { ok = true });
    }

    [HttpPut("/api/cp/servers/{id:guid}/startup-model")]
    public IActionResult SetStartupModel(Guid id, [FromBody] SetStartupModelRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
            return BadRequest(new { error = "modelName is required." });
        if (persistentData.GetServer(id) is null)
            return NotFound(new { error = "Server not found." });
        persistentData.SetServerCurrentModel(id, req.ModelName);
        return Ok(new { ok = true });
    }

    [HttpPut("notes")]
    public IActionResult SaveNotes([FromBody] ModelNotesRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
            return BadRequest(new { error = "modelName is required." });

        persistentData.SetNote(req.ModelName, req.Notes ?? "");
        return Ok(new { ok = true });
    }
}

public record SwitchModelRequest(Guid ServerId, string ModelName);
public record SetStartupModelRequest(string ModelName);
public record ModelNotesRequest(string ModelName, string? Notes);

public record ConventionsRequest(string? Text);

public record TrainRequest(
    string ModelName,
    string StagingPath,
    int    Epochs          = 100,
    int    SaveEveryNEpochs = 10);

public record SpeakRequest(string Text, string? ModelName = null);
public record SplitSentencesRequest(string Text);

using ARI.LLM;
using ARI.VoiceSynthesis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace ARI.WebPanel.Controllers;

public class ControlPanelController : Controller
{
    public IActionResult Index() => View();
}

[Route("api/cp")]
[ApiController]
public class ControlPanelApiController(LlmServiceHolder holder, WebPanelConfig config, SystemInfoHolder systemInfo) : ControllerBase
{
    private LlmService? Llm => holder.Service;

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

        string logPath = config.LogPath;
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

        return Ok(new { ramBytes = bytes, ramMb = bytes / 1024.0 / 1024.0, liveCalls });
    }

    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        long ramBytes = systemInfo.GetTotalRamBytes();

        List<object> callStats = new();
        if (Llm is not null)
        {
            foreach (LlmService.LlmCallStat c in Llm.CallStats())
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
            Agent? dialogueAgent = Llm.Agents.GetValueOrDefault("Dialogue");
            if (dialogueAgent is not null)
            {
                foreach (KeyValuePair<string, ARI.LLM.Thread> kvp in dialogueAgent.Threads)
                {
                    (int used, int limit) = kvp.Value.GetContextStats();
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
}

// ── Voice Synthesis API ───────────────────────────────────────────────────────

[Route("api/cp/voice")]
[ApiController]
public class VoiceController(
    VoiceTrainerHolder voiceHolder,
    DiscordServiceHolder discordHolder,
    WebPanelConfig config,
    ILoggerFactory loggerFactory,
    IHostApplicationLifetime lifetime) : ControllerBase
{
    private readonly ILogger logger = loggerFactory.CreateLogger("ARI.WebPanel");
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
        if (string.IsNullOrEmpty(config.F5Path) || string.IsNullOrEmpty(config.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis module is not configured." });

        TrainingJob job;
        try
        {
            F5Trainer trainer = new(
                f5Path:          config.F5Path,
                voicesPath:      config.VoicesPath,
                audioPath:       req.StagingPath,
                modelName:       req.ModelName,
                epochs:          req.Epochs,
                saveEveryNEpochs: req.SaveEveryNEpochs,
                logger:          logger);

            job = voiceHolder.Start(trainer, req.ModelName, lifetime.ApplicationStopping);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        logger.LogInformation(
            "[Voice] Training started — model: {ModelName}, epochs: {Epochs}",
            req.ModelName, req.Epochs);

        // Delete staging dir and send Discord notification when job finishes
        string stagingPath = req.StagingPath;
        string modelName   = req.ModelName;
        _ = Task.Run(async () =>
        {
            while (job.IsRunning)
                await Task.Delay(2000);
            try { Directory.Delete(stagingPath, recursive: true); }
            catch { /* best-effort */ }

            if (job.IsSuccess)
            {
                logger.LogInformation("[Voice] Voice Synthesis of {ModelName} complete", modelName);
                await discordHolder.NotifyOwner($"> Voice Synthesis of {modelName} complete");
            }
            else
            {
                logger.LogWarning("[Voice] Training failed for {ModelName}: {Error}", modelName, job.Error);
            }
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

        var job = voiceHolder.Current;
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
        var job = voiceHolder.Current;
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

    [HttpGet("models")]
    public IActionResult GetModels()
    {
        if (string.IsNullOrEmpty(config.VoicesPath) || !Directory.Exists(config.VoicesPath))
            return Ok(new { models = Array.Empty<string>() });

        string[] models = Directory.GetDirectories(config.VoicesPath)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .OrderBy(n => n)
            .ToArray()!;

        return Ok(new { models });
    }
}

public record TrainRequest(
    string ModelName,
    string StagingPath,
    int    Epochs          = 100,
    int    SaveEveryNEpochs = 10);

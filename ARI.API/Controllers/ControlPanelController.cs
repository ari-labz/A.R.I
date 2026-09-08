using ARI.API.Data;
using ARI.Common;
using ARI.ImageGen;
using ARI.LLM;
using ARI.Voice;
using ARI.VoiceSynthesis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ARI.API.Controllers;

public class ControlPanelController : Controller
{
    // Control panel UI is served as a static file at /controlpanel.html (from ARI.UI/public/).
    // This Razor route is unused — redirect so nothing breaks if someone hits /ControlPanel.
    public IActionResult Index() => Redirect("/controlpanel.html");
}

[Route("admin")]
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

    // ── User display name ─────────────────────────────────────────────────────────────
    [HttpGet("username")]
    public IActionResult GetUserName() => Ok(new { name = UserNameStore.Get() });

    [HttpPost("username")]
    public IActionResult SetUserName([FromBody] UserNameRequest req)
    {
        UserNameStore.Set(req.Name);
        return Ok(new { ok = true });
    }

    // ── Safe-mode prompt ──────────────────────────────────────────────────────────────
    [HttpGet("safemode-prompt")]
    public IActionResult GetSafeModePrompt() => Ok(new { text = SafeModePromptStore.Get() });

    [HttpPost("safemode-prompt")]
    public IActionResult SetSafeModePrompt([FromBody] SafeModePromptRequest req)
    {
        SafeModePromptStore.Set(req.Text);
        return Ok(new { ok = true });
    }

    // ── Persona ───────────────────────────────────────────────────────────────────────
    [HttpGet("persona")]
    public IActionResult GetPersona() => Ok(new { text = PersonaStore.Get() });

    [HttpPost("persona")]
    public IActionResult SetPersona([FromBody] PersonaRequest req)
    {
        PersonaStore.Set(req.Text ?? "");
        return Ok(new { ok = true });
    }

    // ── Dreaming ──────────────────────────────────────────────────────────────────────

    [HttpGet("dreaming")]
    public IActionResult GetDreaming()
    {
        ILLMModule? llm = Modules.Llm;
        if (llm is null) return StatusCode(503, "LLM is not available.");
        return Ok(new { enabled = llm.DreamingEnabled });
    }

    [HttpPost("dreaming")]
    public IActionResult SetDreaming([FromBody] DreamingRequest req)
    {
        ILLMModule? llm = Modules.Llm;
        if (llm is null) return StatusCode(503, "LLM is not available.");
        llm.DreamingEnabled = req.Enabled;
        return Ok(new { ok = true });
    }

    // ── Calendar ──────────────────────────────────────────────────────────────────────

    /// <summary>Either pass `from`/`to` to browse an explicit window (what the control panel's
    /// month/week/day views use), or `days` to look forward/back from today (what a quick glance,
    /// or nothing at all, defaults to).</summary>
    [HttpGet("calendar")]
    public IActionResult GetCalendar([FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null, [FromQuery] int days = 42)
    {
        ICalendarModule? calendar = Modules.Calendar;
        if (calendar is null) return StatusCode(503, "Calendar is not available.");

        if (from is DateTime start && to is DateTime end)
            return Ok(new
            {
                events    = calendar.ListEventsInRange(start, end).Select(ToJson),
                reminders = calendar.ListRemindersInRange(start, end).Select(ToJson),
            });

        return Ok(new
        {
            events    = calendar.ListEvents(days).Select(ToJson),
            reminders = calendar.ListReminders(days).Select(ToJson),
        });
    }

    [HttpGet("calendar/event/{id:long}")]
    public IActionResult GetCalendarEvent(long id)
    {
        ICalendarModule? calendar = Modules.Calendar;
        if (calendar is null) return StatusCode(503, "Calendar is not available.");
        CalendarEventInfo? found = calendar.GetEvent(id);
        return found is null ? NotFound() : Ok(ToJson(found));
    }

    [HttpGet("calendar/reminder/{id:long}")]
    public IActionResult GetCalendarReminder(long id)
    {
        ICalendarModule? calendar = Modules.Calendar;
        if (calendar is null) return StatusCode(503, "Calendar is not available.");
        ReminderInfo? found = calendar.GetReminder(id);
        return found is null ? NotFound() : Ok(ToJson(found));
    }

    [HttpPost("calendar/event")]
    public IActionResult CreateCalendarEvent([FromBody] CalendarEventRequest req)
    {
        ICalendarModule? calendar = Modules.Calendar;
        if (calendar is null) return StatusCode(503, "Calendar is not available.");
        if (string.IsNullOrWhiteSpace(req.Title)) return BadRequest(new { error = "title is required." });
        long id = calendar.CreateEvent(req.Title, req.Start, req.End, req.IsWholeDay, req.Notes, FromRequest(req.Recurrence));
        return Ok(new { id });
    }

    [HttpPut("calendar/event/{id:long}")]
    public IActionResult UpdateCalendarEvent(long id, [FromBody] CalendarEventRequest req)
    {
        ICalendarModule? calendar = Modules.Calendar;
        if (calendar is null) return StatusCode(503, "Calendar is not available.");
        if (string.IsNullOrWhiteSpace(req.Title)) return BadRequest(new { error = "title is required." });
        return calendar.UpdateEvent(id, req.Title, req.Start, req.End, req.IsWholeDay, req.Notes, FromRequest(req.Recurrence))
            ? Ok(new { ok = true }) : NotFound();
    }

    [HttpPost("calendar/reminder")]
    public IActionResult CreateCalendarReminder([FromBody] CalendarReminderRequest req)
    {
        ICalendarModule? calendar = Modules.Calendar;
        if (calendar is null) return StatusCode(503, "Calendar is not available.");
        if (string.IsNullOrWhiteSpace(req.Title))  return BadRequest(new { error = "title is required." });
        if (string.IsNullOrWhiteSpace(req.Prompt)) return BadRequest(new { error = "prompt is required." });
        long id = calendar.CreateReminder(req.Title, req.TriggerTime, req.Prompt, req.Context, req.Notes, FromRequest(req.Recurrence));
        return Ok(new { id });
    }

    [HttpPut("calendar/reminder/{id:long}")]
    public IActionResult UpdateCalendarReminder(long id, [FromBody] CalendarReminderRequest req)
    {
        ICalendarModule? calendar = Modules.Calendar;
        if (calendar is null) return StatusCode(503, "Calendar is not available.");
        if (string.IsNullOrWhiteSpace(req.Title))  return BadRequest(new { error = "title is required." });
        if (string.IsNullOrWhiteSpace(req.Prompt)) return BadRequest(new { error = "prompt is required." });
        return calendar.UpdateReminder(id, req.Title, req.TriggerTime, req.Prompt, req.Context, req.Notes, FromRequest(req.Recurrence))
            ? Ok(new { ok = true }) : NotFound();
    }

    [HttpDelete("calendar/entry/{id:long}")]
    public IActionResult DeleteCalendarEntry(long id)
    {
        ICalendarModule? calendar = Modules.Calendar;
        if (calendar is null) return StatusCode(503, "Calendar is not available.");
        return calendar.DeleteEntry(id) ? Ok(new { ok = true }) : NotFound();
    }

    private static RecurrenceInfo? FromRequest(RecurrenceRequest? req)
    {
        if (req is null || string.Equals(req.Frequency, "none", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Enum.TryParse(req.Frequency, true, out RecurrenceFrequency frequency)) return null;
        List<DayOfWeek>? days = req.DaysOfWeek?.Select(d => Enum.Parse<DayOfWeek>(d, true)).ToList();
        return new RecurrenceInfo(frequency, Math.Max(1, req.Interval), days, req.Until, req.Count);
    }

    private static object ToJson(RecurrenceInfo? recurrence) => recurrence is null
        ? new { frequency = "none" }
        : new
        {
            frequency  = recurrence.Frequency.ToString(),
            interval   = recurrence.Interval,
            daysOfWeek = recurrence.DaysOfWeek?.Select(d => d.ToString()),
            until      = recurrence.Until,
            count      = recurrence.Count,
        };

    private static object ToJson(CalendarEventInfo e) => new
    {
        id = e.Id, title = e.Title, notes = e.Notes, start = e.Start, end = e.End,
        isWholeDay = e.IsWholeDay, recurrence = ToJson(e.Recurrence),
    };

    private static object ToJson(ReminderInfo r) => new
    {
        id = r.Id, title = r.Title, notes = r.Notes, triggerTime = r.TriggerTime,
        prompt = r.Prompt, context = r.Context, recurrence = ToJson(r.Recurrence),
    };

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
            foreach (KeyValuePair<string, ARI.LLM.Thread> kvp in Llm.Threads
                         .Where(t => t.Value.Pipeline == ARI.LLM.ThreadPipeline.Code || t.Value.Pipeline == ARI.LLM.ThreadPipeline.Dialogue))
            {
                (int used, int limit) = Llm.GetContextStats(kvp.Key);
                if (used <= 0) continue;
                string agentName = kvp.Value.Pipeline == ARI.LLM.ThreadPipeline.Code ? "Code" : "Dialogue";
                context.Add(new { threadKey = kvp.Key, agentName, used, limit, pct = limit > 0 ? (int)(used * 100.0 / limit) : 0 });
            }
        }

        var breakdown = systemInfo.GetRamBreakdown()
            .Select(s => new { label = s.Label, serverName = s.ServerName, mb = Math.Round(s.Bytes / 1024.0 / 1024.0, 1) })
            .ToList();

        double swapMb = systemInfo.GetSwapMb();
        return Ok(new { ramBytes = bytes, ramMb = bytes / 1024.0 / 1024.0, swapMb, liveCalls, context, breakdown });
    }

    [HttpGet("hardware")]
    public IActionResult GetHardware()
    {
        long totalRam = systemInfo.GetTotalPhysicalRamBytes();
        List<SystemInfo.GpuInfo> gpus = systemInfo.GetGpus();
        return Ok(new
        {
            totalRamBytes = totalRam,
            gpus = gpus.Select(g => new { name = g.Name, vramBytes = g.VramBytes }).ToList()
        });
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
            foreach (KeyValuePair<string, ARI.LLM.Thread> kvp in Llm.Threads
                         .Where(t => t.Value.Pipeline == ARI.LLM.ThreadPipeline.Code || t.Value.Pipeline == ARI.LLM.ThreadPipeline.Dialogue))
            {
                (int used, int limit) = Llm.GetContextStats(kvp.Key);
                if (used <= 0) continue;
                string agentName = kvp.Value.Pipeline == ARI.LLM.ThreadPipeline.Code ? "Code" : "Dialogue";
                contextStats.Add(new
                {
                    threadKey = kvp.Key,
                    agentName,
                    used,
                    limit,
                    pct = limit > 0 ? (int)(used * 100.0 / limit) : 0,
                });
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

    // ── llama.cpp management ─────────────────────────────────────────────────

    [HttpGet("llamacpp/status")]
    public IActionResult GetLlamaCppStatus()
    {
        LlamaCppStatus s = Shared.LlamaCpp;
        return Ok(new
        {
            installPath = s.InstallPath,
            installedVersion = s.InstalledVersion,
            latestVersion = s.LatestVersion,
            managedByAri = s.ManagedByAri,
            updateAvailable = s.UpdateAvailable,
            suppressUpdatePrompt = s.SuppressUpdatePrompt,
            currentServer = Shared.LlamaServer,
        });
    }

    [HttpPost("llamacpp/update")]
    public async Task<IActionResult> UpdateLlamaCpp()
    {
        if (Shared.LlamaCppUpdate is null) return StatusCode(503, new { error = "Not available." });
        string? version = await Shared.LlamaCppUpdate();
        return Ok(new { version, server = Shared.LlamaServer });
    }

    [HttpPost("llamacpp/set-path")]
    public IActionResult SetLlamaCppPath([FromBody] LlamaCppPathRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { error = "Path is required." });
        Shared.LlamaCppSetPath?.Invoke(req.Path);
        return Ok(new { path = req.Path });
    }

    [HttpPost("llamacpp/suppress-updates")]
    public IActionResult SuppressLlamaCppUpdates()
    {
        Shared.LlamaCppSuppressUpdates?.Invoke();
        return Ok();
    }

    public sealed class LlamaCppPathRequest { public string Path { get; set; } = ""; }

}

// ── Agents API ────────────────────────────────────────────────────────────────

[Route("agents")]
[ApiController]
public class AgentsApiController(PersistentData persistentData) : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    [HttpGet]
    public IActionResult GetAgents()
    {
        IReadOnlyList<AgentDefinition> agents = persistentData.GetAgents();
        SharedPromptsFile shared = persistentData.GetSharedPrompts();
        var servers = persistentData.GetServers().Select(s => new { name = s.Name, slots = s.Slots });
        return Ok(new { agents, shared, servers });
    }

    /// <summary>Saves the prompts owned by no single agent (the MemoryAgent block, the Budgets footer).
    /// Read at startup, so it takes effect on restart.</summary>
    [HttpPut("shared")]
    public IActionResult UpdateShared([FromBody] SharedPromptsFile req)
    {
        persistentData.UpdateSharedPrompts(req);
        return Ok(new { ok = true, restartRequired = true });
    }

    [HttpPut("{name}")]
    public IActionResult UpdateAgent(string name, [FromBody] AgentDefinition req)
    {
        req.Name = name;

        if (!persistentData.UpdateAgent(req))
            return NotFound(new { error = $"Agent '{name}' not found." });

        if (Llm is not null)
        {
            Llm.AssignAgentServer(name, req.ServerName);
            if (req.SlotName is { Length: > 0 }) Llm.AssignAgentSlot(name, req.SlotName);
        }

        return NoContent();
    }
}

// ── Voice Synthesis API ───────────────────────────────────────────────────────

[Route("voice")]
[ApiController]
public class VoiceController(
    VoiceSynthesisConfig vsConfig,
    ILoggerFactory loggerFactory,
    IHostApplicationLifetime lifetime,
    PersistentData persistentData) : ControllerBase
{
    private readonly ILogger logger = loggerFactory.CreateLogger("ARI.WebPanel");
    private VoiceSynthesisModule? voiceTraining => (VoiceSynthesisModule?)Modules.VoiceSynthesis;
    private VoiceModule?          voiceService  => (VoiceModule?)Modules.Voice;
    private LLMModule?            llm           => (LLMModule?)Modules.Llm;
    private const int DATASET_POLL_MS = 2000;
    private static readonly string StagingRoot = Path.Combine(Path.GetTempPath(), "ari-voice-staging");
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> chunkCounters = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> assembleLocks = new();

    /// <summary>Wipes all staging data. Called on startup so uploads/processing output
    /// never persist across restarts or reboots — the user keeps only the zip they download.</summary>
    public static void ClearStaging()
    {
        try { if (Directory.Exists(StagingRoot)) Directory.Delete(StagingRoot, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

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
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis module is not configured." });

        TrainingJob job;
        try
        {
            if (voiceTraining?.IsSetupComplete != true)
                return StatusCode(503, new { error = "Voice module is still installing. Please wait." });

            string engine = req.Engine ?? "StyleTTS2";
            int port = engine switch { "IndexTTS" => 8026, _ => 8021 };
            string baseUrl = $"http://localhost:{port}";

            // Copy staging data into voice dir so it survives for resume
            string voiceDir = Path.Combine(vsConfig.VoicesPath, engine, req.ModelName);
            string dataDir = Path.Combine(voiceDir, "Data");
            Directory.CreateDirectory(dataDir);
            foreach (string file in Directory.GetFiles(req.StagingPath))
                System.IO.File.Copy(file, Path.Combine(dataDir, Path.GetFileName(file)), overwrite: true);

            IVoiceTrainer trainer = new VoiceModuleTrainer(
                baseUrl:      baseUrl,
                audioPath:    dataDir,
                voiceDir:     voiceDir,
                modelName:    req.ModelName,
                epochs:       req.Epochs,
                saveEvery:    req.SaveEveryNEpochs,
                retrain:      false,
                transcripts:  req.Transcripts,
                phonemeSubs:  PhonemeSubstitutions.Path,
                logger:       logger);

            job = voiceTraining!.Start(trainer, req.ModelName, lifetime.ApplicationStopping);

            TrainingSettings initialSettings = new TrainingSettings(dataDir, req.ModelName, req.Epochs, req.SaveEveryNEpochs, req.Transcripts);
            System.IO.File.WriteAllText(
                Path.Combine(voiceDir, "training.json"),
                JsonSerializer.Serialize(initialSettings));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        logger.LogInformation(
            "[Voice] Training started — engine: {Engine}, model: {ModelName}, epochs: {Epochs}",
            req.Engine, req.ModelName, req.Epochs);

        string stagingPath = req.StagingPath;
        string modelName   = req.ModelName;
        _ = Task.Run(async () =>
        {
            while (job.IsRunning)
                await Task.Delay(2000);

            if (job.IsSuccess)
            {
                try { Directory.Delete(stagingPath, recursive: true); }
                catch { /* best-effort */ }
                logger.LogInformation("[Voice] Voice Synthesis of {ModelName} complete", modelName);
                if (Modules.Discord is not null)
                    await Modules.Discord.NotifyOwner($"> Voice Synthesis of {modelName} complete");
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

        TrainingJob? job = voiceTraining?.Current;
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
            IReadOnlyList<TrainingProgressEvent> events = job.Events;
            bool wrote = false;
            while (sent < events.Count)
            {
                TrainingProgressEvent ev   = events[sent++];
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

            // Send an SSE comment every 30 s so a reverse proxy/tunnel in front of ARI doesn't close the idle connection
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
        TrainingJob? job = voiceTraining?.Current;
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

        Dictionary<string, object> engineParams = new Dictionary<string, object>();
        engineParams["diffusionSteps"] = req.DiffusionSteps;
        engineParams["alpha"]          = req.Alpha;
        engineParams["beta"]           = req.Beta;
        engineParams["embeddingScale"] = req.EmbeddingScale;
        engineParams["speed"]          = req.Speed;
        engineParams["pauseScale"]     = req.PauseScale;
        if (!string.IsNullOrWhiteSpace(req.CheckpointPath))
        {
            if (!System.IO.File.Exists(req.CheckpointPath))
                return NotFound(new { error = $"Checkpoint not found: {req.CheckpointPath}" });
            engineParams["checkpointPath"] = req.CheckpointPath;
        }

        byte[] wav = await voiceService.Synthesise(req.Text, engineParams.Count > 0 ? engineParams : null, ct);
        logger.LogInformation("[Voice/Speak] '{Text}' → {Bytes} bytes", req.Text, wav.Length);
        return File(wav, "audio/wav");
    }

    [HttpGet("settings")]
    public IActionResult GetVoiceSettings()
    {
        if (voiceService is null)
            return StatusCode(503, new { error = "Voice module is not running." });
        (float speed, float pauseScale) = voiceService.GetVoiceSettings();
        return Ok(new { speed, pauseScale });
    }

    [HttpPost("settings")]
    public IActionResult SetVoiceSettings([FromBody] VoiceSettingsRequest req)
    {
        if (voiceService is null)
            return StatusCode(503, new { error = "Voice module is not running." });
        voiceService.SetVoiceSettings(req.Speed, req.PauseScale);
        logger.LogInformation("[Voice/Settings] speed={Speed} pause={Pause}", req.Speed, req.PauseScale);
        return Ok(new { speed = req.Speed, pauseScale = req.PauseScale });
    }

    [HttpGet("{modelName}/checkpoints")]
    public IActionResult GetCheckpoints(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || modelName.Contains('/') || modelName.Contains('\\'))
            return BadRequest(new { error = "Invalid model name." });
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis not configured." });

        string modelDir = Path.Combine(vsConfig.VoicesPath, "StyleTTS2", modelName);
        if (!Directory.Exists(modelDir))
            return NotFound(new { error = $"Model '{modelName}' not found." });

        List<object> checkpoints = new List<object>();

        string modelPth = Path.Combine(modelDir, "model.pth");
        if (System.IO.File.Exists(modelPth))
            checkpoints.Add(new { label = "latest", epoch = (int?)null, path = modelPth });

        string checkpointsDir = Path.Combine(modelDir, "Checkpoints");
        if (Directory.Exists(checkpointsDir))
        {
            var epochEntries = Directory.GetDirectories(checkpointsDir)
                .Select(d => {
                    string folderName = Path.GetFileName(d);
                    string numPart = folderName.Replace("_epochs", "").Trim();
                    if (!int.TryParse(numPart, out int epoch)) return null;
                    string? pth = Directory.GetFiles(d, "*.pth").FirstOrDefault();
                    return pth is null ? null : new { label = $"epoch {epoch}", epoch = (int?)epoch, path = pth };
                })
                .Where(e => e is not null)
                .OrderByDescending(e => e!.epoch)
                .ToList();
            checkpoints.AddRange(epochEntries!);
        }

        return Ok(new { checkpoints });
    }

    [HttpPost("split-sentences")]
    public IActionResult SplitSentences([FromBody] SplitSentencesRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "text is required." });
        IReadOnlyList<string> sentences = ARI.Voice.SentenceSplitter.Split(req.Text);
        return Ok(new { sentences });
    }

    [HttpGet("active")]
    public IActionResult GetActive() =>
        Ok(new
        {
            model        = voiceService?.ActiveModel,
            engine       = voiceService?.ActiveEngine,
            ready        = voiceService?.IsReady ?? false,
            defaultModel = voiceService is not null ? persistentData.GetDefaultVoiceModel(voiceService.ActiveEngine) : null,
        });

    [HttpGet("engine-params")]
    public IActionResult GetEngineParams()
    {
        if (voiceService is null)
            return StatusCode(503, new { error = "Voice module is not running." });
        var parameters = voiceService.GetEngineParameters().Select(p => new
        {
            p.Id, p.Label, p.Min, p.Max, p.Default, p.Step
        });
        return Ok(new { engine = voiceService.ActiveEngine, parameters });
    }

    [HttpPost("switch")]
    public async Task<IActionResult> SwitchEngineModel([FromBody] SwitchEngineRequest req)
    {
        if (voiceService is null)
            return StatusCode(503, new { error = "Voice module is not running." });
        if (string.IsNullOrWhiteSpace(req.Engine) || string.IsNullOrWhiteSpace(req.ModelName))
            return BadRequest(new { error = "engine and modelName are required." });
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis not configured." });

        string modelDir = Path.Combine(vsConfig.VoicesPath, req.Engine, req.ModelName);
        if (!Directory.Exists(modelDir))
            return NotFound(new { error = $"Model '{req.ModelName}' not found for engine {req.Engine}." });

        try
        {
            await voiceService.SwitchEngine(req.Engine, req.ModelName);
            persistentData.SetDefaultVoiceModel(req.Engine, req.ModelName);
            return Ok(new { ok = true, engine = req.Engine, model = req.ModelName });
        }
        catch (Exception ex)
        {
            logger.LogError("[Voice] Engine switch failed: {Error}", ex.Message);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPut("default")]
    public IActionResult SetDefaultModel([FromBody] SetDefaultVoiceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName) || req.ModelName.Contains('/') || req.ModelName.Contains('\\'))
            return BadRequest(new { error = "Invalid model name." });
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis not configured." });

        string engine = req.Engine ?? "StyleTTS2";
        if (!Directory.Exists(Path.Combine(vsConfig.VoicesPath, engine, req.ModelName)))
            return NotFound(new { error = $"Model '{req.ModelName}' not found." });

        persistentData.SetDefaultVoiceModel(engine, req.ModelName);
        logger.LogInformation("[Voice] Default startup voice set to {Model} ({Engine})", req.ModelName, engine);
        return Ok(new { ok = true, defaultModel = req.ModelName });
    }

    [HttpGet("models")]
    public IActionResult GetModels()
    {
        if (string.IsNullOrEmpty(vsConfig.VoicesPath) || !Directory.Exists(vsConfig.VoicesPath))
            return Ok(new { models = Array.Empty<object>() });

        List<object> models = new List<object>();
        foreach (string engineDir in Directory.GetDirectories(vsConfig.VoicesPath))
        {
            string engine = Path.GetFileName(engineDir);
            foreach (string dir in Directory.GetDirectories(engineDir))
            {
                string? name = Path.GetFileName(dir);
                if (name is null) continue;
                string settingsFile = Path.Combine(dir, "training.json");
                TrainingSettings? settings = null;
                if (System.IO.File.Exists(settingsFile))
                {
                    try { settings = JsonSerializer.Deserialize<TrainingSettings>(System.IO.File.ReadAllText(settingsFile)); }
                    catch { }
                }
                models.Add(new
                {
                    name,
                    engine,
                    hasModel     = System.IO.File.Exists(Path.Combine(dir, "model.pth")),
                    hasResume    = settings is not null,
                    audioPath    = settings?.AudioPath,
                    epochs       = settings?.Epochs,
                    saveEveryN   = settings?.SaveEveryNEpochs,
                    latestEpoch  = LatestSavedEpoch(dir),
                });
            }
        }

        return Ok(new { models = models.OrderBy(m => ((dynamic)m).engine).ThenBy(m => ((dynamic)m).name).ToArray() });
    }

    [HttpDelete("{engine}/{modelName}")]
    public IActionResult DeleteModel(string engine, string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || modelName.Contains('/') || modelName.Contains('\\'))
            return BadRequest(new { error = "Invalid model name." });
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis not configured." });

        string dir = Path.Combine(vsConfig.VoicesPath, engine, modelName);
        if (!Directory.Exists(dir))
            return NotFound(new { error = $"Model '{modelName}' not found." });

        Directory.Delete(dir, recursive: true);
        logger.LogInformation("[Voice] Deleted {Engine} model '{ModelName}'", engine, modelName);
        return Ok(new { deleted = modelName, engine });
    }

    [HttpPost("{engine}/{modelName}/dismiss")]
    public IActionResult DismissResume(string engine, string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || modelName.Contains('/') || modelName.Contains('\\'))
            return BadRequest(new { error = "Invalid model name." });
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis not configured." });

        string dir = Path.Combine(vsConfig.VoicesPath, engine, modelName);
        if (!Directory.Exists(dir))
            return NotFound(new { error = $"Model '{modelName}' not found." });

        string settingsFile = Path.Combine(dir, "training.json");
        if (System.IO.File.Exists(settingsFile))
            System.IO.File.Delete(settingsFile);

        logger.LogInformation("[Voice] Dismissed resume for {Engine} model '{ModelName}'", engine, modelName);
        return Ok(new { dismissed = modelName, engine });
    }

    [HttpPost("{modelName}/stop")]
    public async Task<IActionResult> StopTraining(string modelName, [FromQuery] string? engine = "StyleTTS2")
    {
        engine ??= "StyleTTS2";
        int port = engine switch { "IndexTTS" => 8026, _ => 8021 };
        try
        {
            using HttpClient http = new();
            HttpResponseMessage resp = await http.PostAsync($"http://localhost:{port}/train/pause", null);
            resp.EnsureSuccessStatusCode();
            logger.LogInformation("[Voice] Pause requested for model '{ModelName}' on {Engine}", modelName, engine);
            return Ok(new { stopping = modelName });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Voice] Failed to pause training for '{ModelName}'", modelName);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // Returns the highest epoch number found in the model's Checkpoints folder,
    // or from loose epoch_2nd_*.pth files if Checkpoints doesn't exist yet.
    private static int? LatestSavedEpoch(string modelDir)
    {
        // Check organised Checkpoints/<N>_epochs/ folder names first
        string checkpointsDir = Path.Combine(modelDir, "Checkpoints");
        if (Directory.Exists(checkpointsDir))
        {
            int? fromFolders = Directory.GetDirectories(checkpointsDir)
                .Select(d => {
                    string folderName = Path.GetFileName(d);
                    string numPart = folderName.Replace("_epochs", "").Trim();
                    return int.TryParse(numPart, out int n) ? (int?)n : null;
                })
                .Where(n => n is not null)
                .OrderByDescending(n => n)
                .FirstOrDefault();
            if (fromFolders is not null) return fromFolders;
        }

        // Fall back to loose epoch_2nd_NNNNN.pth files (mid-training)
        return Directory.GetFiles(modelDir, "epoch_2nd_*.pth")
            .Select(f => {
                string num = Path.GetFileNameWithoutExtension(f).Split('_').Last();
                return int.TryParse(num, out int n) ? (int?)(n + 1) : null;
            })
            .Where(n => n is not null)
            .OrderByDescending(n => n)
            .FirstOrDefault();
    }

    [HttpGet("{engine}/{modelName}/loss-history")]
    public IActionResult GetLossHistory(string engine, string modelName)
    {
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return Ok(new { points = Array.Empty<object>(), pauses = Array.Empty<int>() });

        string logPath = Path.Combine(vsConfig.VoicesPath, engine, modelName, "Train.log");
        if (!System.IO.File.Exists(logPath))
            return Ok(new { points = Array.Empty<object>(), pauses = Array.Empty<int>() });

        List<object> points    = new List<object>();
        List<int> pauses    = new List<int>();
        int lastEpoch = 0;
        // Epoch [N/total] — tracks current epoch from log lines
        Regex epochRe   = new Regex(@"Epoch \[(\d+)/", RegexOptions.Compiled);
        // Old-format: "Validation loss: X, Dur loss: Y, F0 loss: Z"
        Regex oldValRe  = new Regex(
            @"Validation loss:\s*([\d.]+),\s*Dur loss:\s*([\d.]+),\s*F0 loss:\s*([\d.]+)",
            RegexOptions.Compiled);

        try
        {
            foreach (string rawLine in System.IO.File.ReadLines(logPath))
            {
                // Strip INFO timestamp prefix if present
                string line = rawLine;
                if (line.StartsWith("INFO:", StringComparison.Ordinal))
                {
                    int colon = line.IndexOf(": ", 5, StringComparison.Ordinal);
                    if (colon >= 0) line = line[(colon + 2)..];
                }

                // Track epoch from step lines
                Match em = epochRe.Match(line);
                if (em.Success && int.TryParse(em.Groups[1].Value, out int ep))
                    lastEpoch = ep;

                if (rawLine.StartsWith("LOSS_JSON: ", StringComparison.Ordinal))
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(rawLine["LOSS_JSON: ".Length..]);
                        JsonElement root = doc.RootElement;
                        int jsonEp = root.GetProperty("epoch").GetInt32();
                        lastEpoch  = jsonEp;
                        double? sty  = root.TryGetProperty("sty",  out JsonElement styEl)  ? styEl.GetDouble()  : (double?)null;
                        double? diff = root.TryGetProperty("diff", out JsonElement diffEl) ? diffEl.GetDouble() : (double?)null;
                        double? dur  = root.TryGetProperty("dur",  out JsonElement durEl)  ? durEl.GetDouble()  : (double?)null;
                        points.Add(new { epoch = jsonEp, val = root.GetProperty("val").GetDouble(), f0 = root.GetProperty("f0").GetDouble(), sty, diff, dur });
                    }
                    catch { /* skip malformed */ }
                }
                else
                {
                    Match vm = oldValRe.Match(line);
                    if (vm.Success && lastEpoch > 0 && (points.Count == 0 || ((dynamic)points[^1]).epoch != lastEpoch))
                    {
                        points.Add(new
                        {
                            epoch = lastEpoch,
                            val   = double.Parse(vm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                            f0    = double.Parse(vm.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                            sty   = (double?)null,
                            diff  = (double?)null,
                            dur   = (double?)double.Parse(vm.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                        });
                    }
                    else if (line.TrimStart().StartsWith("── Paused", StringComparison.OrdinalIgnoreCase) && lastEpoch > 0)
                    {
                        pauses.Add(lastEpoch);
                    }
                }
            }
        }
        catch { /* file may be locked mid-write — return what we have */ }

        return Ok(new { points, pauses });
    }

    [HttpPost("resume")]
    public IActionResult ResumeTraining([FromBody] ResumeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
            return BadRequest(new { error = "modelName is required." });
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis module is not configured." });
        if (voiceTraining?.IsSetupComplete != true)
            return StatusCode(503, new { error = "Voice module is still installing. Please wait." });

        string engine = req.Engine ?? "StyleTTS2";
        string settingsFile = Path.Combine(vsConfig.VoicesPath, engine, req.ModelName, "training.json");
        if (!System.IO.File.Exists(settingsFile))
            return NotFound(new { error = $"No saved training settings found for '{req.ModelName}'." });

        TrainingSettings? settings;
        try { settings = JsonSerializer.Deserialize<TrainingSettings>(System.IO.File.ReadAllText(settingsFile)); }
        catch { return StatusCode(500, new { error = "Could not parse training settings." }); }
        if (settings is null)
            return StatusCode(500, new { error = "Could not parse training settings." });

        int effectiveEpochs      = req.Epochs      ?? settings.Epochs;
        int effectiveSaveEveryN  = req.SaveEveryNEpochs ?? settings.SaveEveryNEpochs;

        // Persist updated targets so a future resume picks them up
        if (req.Epochs.HasValue || req.SaveEveryNEpochs.HasValue)
        {
            TrainingSettings updated = settings with { Epochs = effectiveEpochs, SaveEveryNEpochs = effectiveSaveEveryN };
            System.IO.File.WriteAllText(settingsFile, JsonSerializer.Serialize(updated));
        }

        if (req.Retrain)
        {
            string modelDir = Path.Combine(vsConfig.VoicesPath, engine, req.ModelName);
            string modelPth = Path.Combine(modelDir, "model.pth");
            string checkpointsDir = Path.Combine(modelDir, "Checkpoints");
            if (System.IO.File.Exists(modelPth)) System.IO.File.Delete(modelPth);
            if (Directory.Exists(checkpointsDir)) Directory.Delete(checkpointsDir, recursive: true);
            logger.LogInformation("[Voice] Retrain requested — cleared checkpoints for '{ModelName}'", req.ModelName);
        }

        TrainingJob job;
        try
        {
            int port = engine switch { "IndexTTS" => 8026, _ => 8021 };
            VoiceModuleTrainer trainer = new(
                baseUrl:      $"http://localhost:{port}",
                audioPath:    settings.AudioPath,
                voiceDir:     Path.Combine(vsConfig.VoicesPath, engine, settings.ModelName),
                modelName:    settings.ModelName,
                epochs:       effectiveEpochs,
                saveEvery:    effectiveSaveEveryN,
                retrain:      req.Retrain,
                transcripts:  settings.Transcripts,
                phonemeSubs:  PhonemeSubstitutions.Path,
                logger:       logger);

            job = voiceTraining!.Start(trainer, settings.ModelName, lifetime.ApplicationStopping);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        logger.LogInformation(
            "[Voice] Resume training started — model: {ModelName}, epochs: {Epochs}",
            settings.ModelName, effectiveEpochs);

        string modelName = settings.ModelName;
        _ = Task.Run(async () =>
        {
            while (job.IsRunning) await Task.Delay(2000);
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
        });

        return Ok(new { jobId = job.JobId, modelName = job.ModelName });
    }

    // ── Audio Transcription ────────────────────────────────────────────────
    // Chunks uploaded audio and transcribes with Whisper so users can review/edit
    // transcripts before training any voice engine.

    [HttpPost("transcribe")]
    public IActionResult StartTranscription([FromBody] DatasetProcessRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.StageId))
            return BadRequest(new { error = "stageId is required." });

        string stageDir = Path.Combine(StagingRoot, req.StageId);
        if (!Directory.Exists(stageDir))
            return BadRequest(new { error = "Unknown stageId. Call /stage first." });
        if (voiceTraining?.IsSetupComplete != true)
            return StatusCode(503, new { error = "StyleTTS2 must be set up first (Whisper is part of its environment)." });

        try
        {
            AudioTranscriber.Start(stageDir, vsConfig.DataDir, logger, lifetime.ApplicationStopping);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        return Ok(new { started = true });
    }

    [HttpGet("transcribe/status")]
    public IActionResult TranscriptionStatus()
    {
        AudioTranscriber? t = AudioTranscriber.Current;
        if (t is null)
            return Ok(new { step = "Idle", percent = 0, running = false, clips = Array.Empty<object>() });

        return Ok(new
        {
            step    = t.Step,
            percent = t.Percent,
            running = t.IsRunning,
            error   = t.Error,
            workDir = t.WorkDir,
            clips   = t.Clips.Select(c => new { c.FileName, c.Transcript, c.Duration }),
        });
    }

    [HttpGet("transcribe/audio")]
    public IActionResult TranscribeAudio([FromQuery] string name)
    {
        AudioTranscriber? t = AudioTranscriber.Current;
        if (t is null)
            return NotFound(new { error = "No transcription in progress." });

        string wavPath = Path.Combine(t.WorkDir, "wavs", name);
        if (!System.IO.File.Exists(wavPath))
            return NotFound(new { error = $"Clip not found: {name}" });

        return PhysicalFile(wavPath, "audio/wav");
    }

    // ── Dataset Builder ───────────────────────────────────────────────────────
    // Processes uploaded clips (demucs + Whisper, split into parts) so the panel can
    // A/B the original against the cleaned variant and download a chosen dataset.

    [HttpPost("dataset/process")]
    public IActionResult ProcessDataset([FromBody] DatasetProcessRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.StageId))
            return BadRequest(new { error = "stageId is required." });

        string stageDir = Path.Combine(StagingRoot, req.StageId);
        if (!Directory.Exists(stageDir))
            return BadRequest(new { error = "Unknown stageId. Call /stage first." });
        if (string.IsNullOrEmpty(vsConfig.StyleTtsPath))
            return StatusCode(503, new { error = "VoiceSynthesis module is not configured." });
        if (voiceTraining?.IsSetupComplete != true)
            return StatusCode(503, new { error = "StyleTTS2 is still installing. Please wait." });

        DatasetBuilder builder;
        try { builder = DatasetBuilder.Start(vsConfig.StyleTtsPath, vsConfig.DataDir, stageDir, !req.Demucs, logger, lifetime.ApplicationStopping); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }

        logger.LogInformation("[Voice] Dataset processing started for stage {StageId}", req.StageId);

        // Free RAM/GPU while demucs + Whisper run, then bring the llama servers back.
        _ = Task.Run(async () =>
        {
            if (llm is not null) await llm.StopAllServersAsync();
            while (builder.IsRunning) await Task.Delay(DATASET_POLL_MS);
            if (llm is not null) await llm.RestartAllServersAsync();
        });

        return Ok(new { started = true });
    }

    [HttpGet("dataset/status")]
    public IActionResult DatasetStatus()
    {
        DatasetBuilder? builder = DatasetBuilder.Current;
        if (builder is null)
            return Ok(new { step = "Idle", percent = 0, running = false, parts = Array.Empty<object>() });

        return Ok(new
        {
            step      = builder.Step,
            percent   = builder.Percent,
            running   = builder.IsRunning,
            success   = builder.IsSuccess,
            error     = builder.Error,
            parts     = builder.Parts,
            hasDemucs = builder.HasDemucs,
        });
    }

    [HttpGet("dataset/audio")]
    public IActionResult DatasetAudio([FromQuery] string name, [FromQuery] string variant)
    {
        string? path = DatasetBuilder.Current?.VariantPath(name, variant);
        return path is null ? NotFound() : PhysicalFile(path, "audio/wav");
    }

    [HttpPost("dataset/split")]
    public IActionResult SplitClip([FromBody] DatasetSplitRequest req)
    {
        DatasetBuilder? builder = DatasetBuilder.Current;
        if (builder is null || builder.IsRunning)
            return BadRequest(new { error = "No completed dataset." });
        try
        {
            List<DatasetPart> parts = builder.Split(req.Name);
            return Ok(new { parts });
        }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    [HttpPost("dataset/unsplit")]
    public IActionResult UnsplitClip([FromBody] DatasetSplitRequest req)
    {
        DatasetBuilder? builder = DatasetBuilder.Current;
        if (builder is null || builder.IsRunning)
            return BadRequest(new { error = "No completed dataset." });
        try
        {
            DatasetPart? part = builder.Unsplit(req.Name);
            return Ok(new { part });
        }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    [HttpPost("dataset/build")]
    public IActionResult BuildDataset([FromBody] DatasetBuildRequest req)
    {
        DatasetBuilder? builder = DatasetBuilder.Current;
        if (builder is null || builder.IsRunning)
            return BadRequest(new { error = "No completed dataset to build." });

        return File(builder.BuildZip(req.Selections), "application/zip", "dataset.zip");
    }
}

// ── LLM Models & Servers API ──────────────────────────────────────────────────

[Route("models")]
[ApiController]
public class ModelsApiController(PersistentData persistentData) : ControllerBase
{
    private LLMModule? llm => (LLMModule?)Modules.Llm;
    private string ModelsPath => llm?.ModelsPath ?? "";

    [HttpGet]
    public IActionResult GetModels()
    {
        Dictionary<string, string> notes      = persistentData.GetAllNotes();
        string mPath   = ModelsPath;

        HashSet<string?> activeModelNames  = llm?.Servers.Select(s => s.ActiveModel?.Name).Where(n => n is not null).ToHashSet() ?? [];
        HashSet<string?> startupModelNames = persistentData.GetServers().Select(s => s.CurrentModelName).Where(n => n is not null).ToHashSet();

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
                fileSizeMb         = m.FileSizeBytes > 0 ? Math.Round(m.FileSizeBytes / 1048576.0) : (double?)null,
                kvArch             = m.KvArch is { } a ? new { nLayers = a.NLayers, nKvHeads = a.NKvHeads, headDim = a.HeadDim } : null,
                moe                = m.MoE,
                mtp                = m.MTP,
                supportsThinking   = m.SupportsThinking,
                supportsReasoningEffort = m.SupportsReasoningEffort,
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
                slots           = s.Slots,
                kvCacheQuantK   = s.KvCacheQuantK,
                kvCacheQuantV   = s.KvCacheQuantV,

                currentModelName = s.CurrentModelName,
                autoStart       = s.BootStartup,
                unifiedCache    = s.UnifiedCache,

                // Sampler defaults — the control panel's server modal edits these directly.
                temperature      = s.Temperature,
                topP             = s.TopP,
                topK             = s.TopK,
                minP             = s.MinP,
                topNSigma        = s.TopNSigma,
                typicalP         = s.TypicalP,
                xtcProbability   = s.XtcProbability,
                xtcThreshold     = s.XtcThreshold,
                dynatempRange    = s.DynatempRange,
                dynatempExp      = s.DynatempExp,
                repeatLastN      = s.RepeatLastN,
                repeatPenalty    = s.RepeatPenalty,
                presencePenalty  = s.PresencePenalty,
                frequencyPenalty = s.FrequencyPenalty,
                dryMultiplier    = s.DryMultiplier,
                dryBase          = s.DryBase,
                dryAllowedLength = s.DryAllowedLength,
                dryPenaltyLastN  = s.DryPenaltyLastN,
                drySequenceBreakers = s.DrySequenceBreakers,
                mirostat         = s.Mirostat,
                mirostatLr       = s.MirostatLr,
                mirostatEnt      = s.MirostatEnt,
                seed             = s.Seed,
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

    [HttpPut("notes")]
    public IActionResult SaveNotes([FromBody] ModelNotesRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
            return BadRequest(new { error = "modelName is required." });

        persistentData.SetNote(req.ModelName, req.Notes ?? "");
        return Ok(new { ok = true });
    }

    /// <summary>Toggle whether this model emits a reasoning chain. Drives --reasoning-format on the
    /// llama-server command line: a non-thinking model whose template lacks a think branch fails to
    /// parse when the flag is passed, so it must be off for those. Takes effect on next server start.</summary>
    [HttpPut("thinking")]
    public IActionResult SetThinking([FromBody] ModelThinkingRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
            return BadRequest(new { error = "modelName is required." });

        Model? model = persistentData.GetModels()
            .FirstOrDefault(m => m.Name.Equals(req.ModelName, StringComparison.OrdinalIgnoreCase));
        if (model is null) return NotFound(new { error = "Model not found." });

        model.SupportsThinking = req.SupportsThinking;
        persistentData.UpdateModel(model);
        return Ok(new { ok = true });
    }

    /// <summary>The one global reasoning-effort dial (0/1/2 = low/medium/xhigh). Universal across all agents;
    /// scales each agent's thinking budget and drives the reasoning_effort wire field on supporting models.
    /// See Documentation/Server/ARI.LLM/Reasoning-Effort.</summary>
    [HttpGet("reasoning-effort")]
    public IActionResult GetReasoningEffort() => Ok(new
    {
        step        = ReasoningEffortStore.Step,
        level       = ReasoningEffortStore.Level,
        levels      = ReasoningEffortStore.Levels,
        multipliers = ReasoningEffortStore.Multipliers,
        // Per-pipeline support: the composer shows the dial only when the model that will actually answer
        // supports reasoning_effort — Dialogue's model in Default mode, Coder's in Code mode.
        support     = new
        {
            dialogue = AgentModelSupportsEffort("Dialogue"),
            coder    = AgentModelSupportsEffort("Coder"),
        },
    });

    /// <summary>Whether the model bound to the named agent's server is online and supports reasoning_effort.</summary>
    private bool AgentModelSupportsEffort(string agentName)
    {
        if (llm is null || !llm.Agents.TryGetValue(agentName, out Agent? agent)) return false;
        Server? srv = llm.Servers.FirstOrDefault(s => s.Name == agent.ServerName);
        return srv?.ActiveModel?.SupportsReasoningEffort == true;
    }

    [HttpPut("reasoning-effort")]
    public IActionResult SetReasoningEffort([FromBody] ReasoningEffortRequest req)
    {
        ReasoningEffortStore.Step = req.Step;
        return Ok(new { ok = true, step = ReasoningEffortStore.Step, level = ReasoningEffortStore.Level });
    }
}

// ── Servers API ───────────────────────────────────────────────────────────────

[Route("servers")]
[ApiController]
public class ServersApiController(PersistentData persistentData) : ControllerBase
{
    private LLMModule? llm => (LLMModule?)Modules.Llm;

    [HttpPost]
    public IActionResult AddServer([FromBody] Server server)
    {
        if (string.IsNullOrWhiteSpace(server.Name))
            return BadRequest(new { error = "name is required." });

        persistentData.AddServer(server);
        llm?.AddServer(server);
        return Ok(server);
    }

    [HttpPut("{id:guid}")]
    public IActionResult UpdateServer(Guid id, [FromBody] Server server)
    {
        Server? existing = persistentData.GetServers().FirstOrDefault(s => s.Id == id);
        if (!persistentData.UpdateServer(server))
            return NotFound(new { error = "Server not found." });
        llm?.UpdateServer(server);

        if (existing is not null && existing.Name != server.Name)
            persistentData.RenameServerInAgents(existing.Name, server.Name);

        return Ok(server);
    }

    [HttpDelete("{id:guid}")]
    public IActionResult DeleteServer(Guid id)
    {
        Server? live = llm?.Servers.FirstOrDefault(s => s.Id == id);
        live?.Stop();
        if (!persistentData.RemoveServer(id))
            return NotFound(new { error = "Server not found." });
        llm?.RemoveServer(id);
        return Ok(new { ok = true });
    }

    [HttpPost("{id:guid}/start")]
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

    [HttpPost("{id:guid}/stop")]
    public IActionResult StopServer(Guid id)
    {
        Server? server = llm?.Servers.FirstOrDefault(s => s.Id == id);
        if (server is null) return NotFound(new { error = "Server not found or LLM module unavailable." });
        server.Stop();
        return Ok(new { ok = true });
    }

    [HttpPost("{id:guid}/restart")]
    public IActionResult RestartServer(Guid id)
    {
        Server? server = llm?.Servers.FirstOrDefault(s => s.Id == id);
        if (server is null) return NotFound(new { error = "Server not found or LLM module unavailable." });
        _ = Task.Run(() => server.RestartAsync());
        return Ok(new { ok = true });
    }

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

    [HttpPut("{id:guid}/startup-model")]
    public IActionResult SetStartupModel(Guid id, [FromBody] SetStartupModelRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
            return BadRequest(new { error = "modelName is required." });
        if (persistentData.GetServer(id) is null)
            return NotFound(new { error = "Server not found." });
        persistentData.SetServerCurrentModel(id, req.ModelName);
        return Ok(new { ok = true });
    }
}

public record SwitchModelRequest(Guid ServerId, string ModelName);
public record SetStartupModelRequest(string ModelName);
public record ModelNotesRequest(string ModelName, string? Notes);
public record ModelThinkingRequest(string ModelName, bool SupportsThinking);
public record ReasoningEffortRequest(int Step);

public record ConventionsRequest(string? Text);
public record PersonaRequest(string? Text);
public record UserNameRequest(string? Name);
public record SafeModePromptRequest(string? Text);
public record DreamingRequest(bool Enabled);

public record RecurrenceRequest(string Frequency, int Interval = 1, List<string>? DaysOfWeek = null, DateTime? Until = null, int? Count = null);
public record CalendarEventRequest(string Title, DateTime Start, DateTime End, bool IsWholeDay, string? Notes, RecurrenceRequest? Recurrence);
public record CalendarReminderRequest(string Title, DateTime TriggerTime, string Prompt, string? Context, string? Notes, RecurrenceRequest? Recurrence);

public record TrainRequest(
    string ModelName,
    string StagingPath,
    string Engine          = "StyleTTS2",
    int    Epochs          = 365,
    int    SaveEveryNEpochs = 10,
    string QuantType       = "q4_k_m",
    Dictionary<string, string>? Transcripts = null);

/// <summary>One of ARI's optional subsystems, as shown on the control panel's Modules tab.</summary>
/// <param name="Key">Key under "Modules" in AriConfig.json.</param>
/// <param name="Name">Human-readable name.</param>
/// <param name="Description">What the module does, in plain English.</param>
/// <param name="Running">Whether it is actually live in this process right now.</param>
/// <param name="Required">Modules that cannot be turned off — switching them off would
/// take the control panel itself away, leaving no way to turn them back on.</param>
public record ModuleInfo(string Key, string Name, string Description, bool Enabled, bool Running, bool Required);

[Route("modules")]
[ApiController]
public class ModulesApiController(ILogger<ModulesApiController> logger) : ControllerBase
{
    // Description and display order for the Modules tab. The keys match AriConfig.json's
    // "Modules" object, which is the only place a module's on/off state is stored.
    private static readonly (string Key, string Name, string Description, bool Required)[] Catalogue =
    [
        ("LLM", "Language model",
         "Runs the local model servers behind every reply Ari gives. With this off Ari cannot think or hold a conversation.", true),
        ("API", "Web control panel",
         "Serves this control panel and the web chat. Turning it off would leave no way to turn it back on.", true),
        ("Voice", "Voice",
         "Speaks Ari's replies out loud using the active speech engine.", false),
        ("VoiceSynthesis", "Voice training",
         "Installs the speech engines and trains new voices from your recordings.", false),
        ("Listener", "Listener",
         "Transcribes your microphone with Whisper so you can talk to Ari instead of typing.", false),
        ("Brain", "Brain",
         "Stores Ari's long-term memory as an Obsidian vault of notes she can search and add to.", false),
        ("Discord", "Discord",
         "Connects Ari to Discord so she can read and reply to messages and join voice channels.", false),
        ("Calendar", "Calendar",
         "Ari's own calendar of events and reminders — events feed her context, reminders trigger a real message from her at their scheduled time.", false),
    ];

    private static bool IsRunning(string key) => key switch
    {
        "LLM"            => Modules.Llm            is not null,
        "Voice"          => Modules.Voice          is not null,
        // VoiceSynthesisModule is always registered (it's also the training-job tracker), so its
        // presence alone doesn't mean the module is "on" — IsSetupComplete does.
        "VoiceSynthesis" => Modules.VoiceSynthesis?.IsSetupComplete ?? false,
        "Listener"       => Modules.Listener       is not null,
        "Brain"          => Modules.Brain          is not null,
        "Discord"        => Modules.Discord        is not null,
        "Calendar"       => Modules.Calendar       is not null,
        // Answering this request is itself proof the web panel is up.
        "API"            => true,
        _                => false,
    };

    [HttpGet]
    public IActionResult GetModules()
    {
        JsonObject? modules = ReadModulesNode(out string? error);
        if (modules is null)
            return StatusCode(500, new { error });

        List<ModuleInfo> result = Catalogue.Select(m =>
        {
            // A module added after someone's AriConfig.json was first written (Calendar, say) has no
            // node in their file at all. ARI.API can't reference ARI.Core's strongly-typed AriConfig
            // to ask what that module's real default is (that dependency runs the other way), so an
            // absent node instead trusts whatever is actually running right now — a module that is
            // live is definitionally enabled, whatever the file does or doesn't say.
            bool? explicitlySet = modules[m.Key]?["Enabled"]?.GetValue<bool>();
            bool running = IsRunning(m.Key);
            return new ModuleInfo(m.Key, m.Name, m.Description,
                Enabled:  explicitlySet ?? running,
                Running:  running,
                Required: m.Required);
        }).ToList();

        return Ok(new { modules = result });
    }

    [HttpPut("{key}")]
    public async Task<IActionResult> SetEnabled(string key, [FromBody] SetModuleEnabledRequest req)
    {
        (string Key, string Name, string Description, bool Required) entry = Catalogue.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (entry.Key is null)
            return NotFound(new { error = $"Unknown module '{key}'." });

        if (entry.Required && !req.Enabled)
            return BadRequest(new { error = $"{entry.Name} cannot be turned off — {entry.Description}" });

        string path = Paths.AriConfig;
        JsonObject? modules = ReadModulesNode(out string? error, out JsonNode? root);
        if (modules is null || root is null)
            return StatusCode(500, new { error });

        // A module added to ARI after this file was first written (Calendar, say) has no node here
        // yet — create one rather than failing, so the very first toggle a user makes on a new
        // module doesn't error out.
        if (modules[entry.Key] is not JsonObject moduleNode)
        {
            moduleNode = new JsonObject();
            modules[entry.Key] = moduleNode;
        }

        moduleNode["Enabled"] = req.Enabled;

        try
        {
            // Rewrite through the parsed tree so every other setting — ports, paths and any
            // ${SECRET} placeholders — comes back out exactly as it went in.
            string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Modules] Failed to write {Path}.", path);
            return StatusCode(500, new { error = $"Could not save AriConfig.json: {ex.Message}" });
        }

        // Apply it live if the module knows how to start/stop itself — most do. A null result means
        // it actually happened; any other string is either "can't do this hot" or a genuine failure,
        // and either way the change is still saved above for next boot.
        string? lifecycleResult = Modules.Lifecycle is { } lifecycle
            ? await (req.Enabled ? lifecycle.StartModule(entry.Key) : lifecycle.StopModule(entry.Key))
            : "No running instance to apply this to.";
        bool appliedNow = lifecycleResult is null;

        logger.LogInformation("[Modules] {Module} set to {State} — {Effect}.", entry.Name, req.Enabled ? "enabled" : "disabled",
            appliedNow ? "applied immediately" : $"takes effect on next restart ({lifecycleResult})");

        return Ok(new { key = entry.Key, enabled = req.Enabled, restartRequired = !appliedNow, message = appliedNow ? null : lifecycleResult });
    }

    private static JsonObject? ReadModulesNode(out string? error) => ReadModulesNode(out error, out _);

    private static JsonObject? ReadModulesNode(out string? error, out JsonNode? root)
    {
        error = null;
        root  = null;
        string path = Paths.AriConfig;

        if (!System.IO.File.Exists(path))
        {
            error = $"No AriConfig.json found at {path}.";
            return null;
        }

        try
        {
            root = JsonNode.Parse(System.IO.File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception ex)
        {
            error = $"Could not read AriConfig.json: {ex.Message}";
            return null;
        }

        if (root?["Modules"] is not JsonObject modules)
        {
            error = "AriConfig.json has no \"Modules\" section.";
            return null;
        }

        return modules;
    }
}

public record SetModuleEnabledRequest(bool Enabled);

public record SpeakRequest(string Text, string? ModelName = null, string? CheckpointPath = null,
    int DiffusionSteps = 5, float Alpha = 0.3f, float Beta = 0.7f, float EmbeddingScale = 1.0f,
    float Speed = 1.0f, float PauseScale = 1.0f);
public record VoiceSettingsRequest(float Speed = 1.0f, float PauseScale = 1.0f);
public record SetDefaultVoiceRequest(string ModelName, string? Engine = null);
public record SwitchEngineRequest(string Engine, string ModelName);
public record SplitSentencesRequest(string Text);
public record ResumeRequest(string ModelName, string? Engine = null, int? Epochs = null, int? SaveEveryNEpochs = null, bool Retrain = false);
public record DatasetProcessRequest(string StageId, bool Demucs = true);
public record DatasetBuildRequest(ARI.VoiceSynthesis.DatasetBuildSelection[] Selections);

// ── Image Generation API ──────────────────────────────────────────────────────

[Route("imagegen")]
[ApiController]
public class ImageGenController(APIConfig config) : ControllerBase
{
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        string path = ARI.Common.Paths.AriConfig;
        if (!System.IO.File.Exists(path)) return NotFound();

        using JsonDocument doc  = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        JsonElement        root = doc.RootElement;

        if (!root.TryGetProperty("Modules", out JsonElement modules) &&
            !root.TryGetProperty("modules", out modules))
            return Ok(new { Enabled = false, Checkpoint = "", Port = 8188, IdleSeconds = 300 });

        if (!modules.TryGetProperty("ImageGen", out JsonElement ig) &&
            !modules.TryGetProperty("imagegen", out ig))
            return Ok(new { Enabled = false, Checkpoint = "", Port = 8188, IdleSeconds = 300 });

        return Ok(new
        {
            Enabled     = ig.TryGetProperty("Enabled",     out JsonElement e)   && e.GetBoolean(),
            Checkpoint  = ig.TryGetProperty("Checkpoint",  out JsonElement c)   ? c.GetString()  ?? "" : "",
            ComfyUiPath = ig.TryGetProperty("ComfyUiPath", out JsonElement cup) ? cup.GetString() ?? "" : "",
            Port        = ig.TryGetProperty("Port",        out JsonElement p)   ? p.GetInt32()        : 8188,
            IdleSeconds = ig.TryGetProperty("IdleSeconds", out JsonElement id)  ? id.GetInt32()       : 300,
        });
    }

    [HttpPost("config")]
    public IActionResult SaveConfig([FromBody] ImageGenConfigRequest req)
    {
        string path = ARI.Common.Paths.AriConfig;
        if (!System.IO.File.Exists(path)) return NotFound();

        JsonNode root = JsonNode.Parse(System.IO.File.ReadAllText(path))!;
        JsonNode modules = root["Modules"] ?? root["modules"]
            ?? throw new Exception("AriConfig.json has no Modules section.");

        JsonNode ig = modules["ImageGen"] ?? modules["imagegen"] ?? new JsonObject();
        ig["Enabled"]      = req.Enabled;
        ig["Checkpoint"]   = req.Checkpoint   ?? "";
        ig["ComfyUiPath"]  = req.ComfyUiPath  ?? "";
        ig["Port"]         = req.Port;
        ig["IdleSeconds"]  = req.IdleSeconds;

        // Write back under whichever key exists
        if (modules["ImageGen"] is not null) modules["ImageGen"] = ig;
        else if (modules["imagegen"] is not null) modules["imagegen"] = ig;
        else modules["ImageGen"] = ig;

        System.IO.File.WriteAllText(path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return Ok();
    }

    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(new
    {
        Ready  = Modules.ImageGen?.IsReady ?? false,
        Active = Modules.ImageGen is not null,
    });

    [HttpGet("models")]
    public IActionResult ListCheckpoints()
    {
        // Walk ComfyUI's models/checkpoints directory and return filenames.
        string comfyPath = Modules.ImageGen is not null
            ? ARI.ImageGen.Dependency.ComfyUiPath ?? ARI.ImageGen.Dependency.DefaultInstallPath()
            : ARI.ImageGen.Dependency.DefaultInstallPath();

        string ckptDir = Path.Combine(comfyPath, "models", "checkpoints");
        if (!Directory.Exists(ckptDir))
            return Ok(Array.Empty<string>());

        string[] files = Directory.GetFiles(ckptDir, "*.safetensors")
            .Concat(Directory.GetFiles(ckptDir, "*.ckpt"))
            .Select(Path.GetFileName)
            .Where(f => f is not null)
            .OrderBy(f => f)
            .ToArray()!;

        return Ok(files);
    }
}

public record ImageGenConfigRequest(bool Enabled, string? Checkpoint, string? ComfyUiPath = null, int Port = 8188, int IdleSeconds = 300);
public record DatasetSplitRequest(string Name);

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ARI.API;
using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ARI.API.Controllers;


[Route("threads")]
[ApiController]
public class ThreadsController(ProjectStore projectStore) : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    // The web client reads SSE events as camelCase (data.type / data.threadKey / data.text). Without
    // this, bare Serialize emits PascalCase ("Type") and the client's event switch silently never matches.
    private static readonly JsonSerializerOptions SseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Maps threadKey → projectId. Lives on ProjectStore (not here) so both this controller and
    // ProjectServiceAdapter — the REST path and the tool-call path — share the exact same state.
    private ConcurrentDictionary<string, string> ThreadProjects => projectStore.ThreadProjects;

    // Pending per-message attachments (ephemeral — cleared after send).
    private static readonly ConcurrentDictionary<string, List<Attachment>> pendingMessageAttachments = new();
    // Filenames of text files written to disk since the last send — injected as a note into platformContext.
    private static readonly ConcurrentDictionary<string, List<string>>     pendingFileNotes          = new();

    // System-context blocks injected by the client (e.g. filesystem skeleton). Merged into the
    // system prompt at send time — never exposed as user-visible thread attachments.
    private static readonly ConcurrentDictionary<string, string> pendingSystemContext = new();

    /// <summary>Derives a display username from the authenticated user's email (strips @domain).</summary>
    private string GetUsername()
    {
        // Proxy auth claim wins if present (Cloudflare Access etc.)
        string? email = User.FindFirstValue(ClaimTypes.Email);
        if (!string.IsNullOrEmpty(email))
        {
            int at = email.IndexOf('@');
            string raw = at > 0 ? email[..at] : email;
            return raw.Length > 0 ? char.ToUpper(raw[0]) + raw[1..] : raw;
        }
        // JWT display name claim (set at login from the user's preferences).
        string? displayName = User.FindFirstValue("displayName");
        if (!string.IsNullOrEmpty(displayName)) return displayName;
        // Username claim as last resort.
        string? username = User.FindFirstValue(ClaimTypes.Name);
        return string.IsNullOrEmpty(username) ? "User" : username;
    }

    // ── Thread navigation helpers ───────────────────────────────────────────────

    /// <summary>True if the current caller may read/write this thread.</summary>
    private bool CanAccessThread(ARI.LLM.Thread thread)
    {
        if (User.FindFirstValue(System.Security.Claims.ClaimTypes.Role) == ARI.API.Auth.Roles.Admin) return true;
        string? callerId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        return thread.OwnerId == callerId;
    }

    /// <summary>Finds an existing user-facing thread (Dialogue or Code) by key.</summary>
    private ARI.LLM.Thread? FindThread(string threadKey)
    {
        if (Llm is null) return null;
        Llm.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? t);
        return t;
    }

    /// <summary>Finds any thread (including internal) by key.</summary>
    private ARI.LLM.Thread? FindAnyThread(string threadKey)
    {
        if (Llm is null) return null;
        Llm.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? t);
        return t;
    }

    /// <summary>Gets or creates the correct thread for the given key, routing to Code or Dialogue.</summary>
    private ARI.LLM.Thread GetOrCreateThread(string threadKey)
    {
        bool isCode = Llm!.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? existing)
                      && existing.Pipeline == ARI.LLM.ThreadPipeline.Code;
        ARI.LLM.Thread thread = isCode ? Llm.GetOrCreateCodeThread(threadKey) : Llm.GetOrCreateDialogueThread(threadKey);
        bool isAdmin = User.FindFirstValue(System.Security.Claims.ClaimTypes.Role) == ARI.API.Auth.Roles.Admin;
        thread.IsOwnerThread = isAdmin;
        // Track who created the thread so guests only see their own.
        thread.OwnerId ??= User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        return thread;
    }

    // ── Thread endpoints ────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult GetThreads([FromQuery] bool includeInternal = false)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        var allThreads = Llm.Threads;

        bool   callerIsAdmin = User.FindFirstValue(System.Security.Claims.ClaimTypes.Role) == ARI.API.Auth.Roles.Admin;
        string? callerId     = callerIsAdmin ? null : User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);

        List<ThreadEntry> threads = allThreads
            .Where(kvp => kvp.Value.Pipeline is ARI.LLM.ThreadPipeline.Dialogue or ARI.LLM.ThreadPipeline.Code or ARI.LLM.ThreadPipeline.Speech
                          && !kvp.Value.Internal
                          && (callerIsAdmin || kvp.Value.OwnerId == callerId))
            .Select(kvp =>
            {
                string? projectId   = ThreadProjects.TryGetValue(kvp.Key, out string? pid) ? pid : null;
                string? projectName = projectId is not null ? projectStore.Get(projectId)?.Name : null;
                return new ThreadEntry(kvp.Key, AgentName: null, IsInternal: false,
                    LastMessageAt: kvp.Value.LastMessageAt,
                    MessageCount: kvp.Value.History.Count(m => m is Prompt or ARI.LLM.Response),
                    State: kvp.Value.State.ToString().ToLowerInvariant(),
                    IsCodeMode: kvp.Value.Pipeline == ARI.LLM.ThreadPipeline.Code,
                    ProjectName: projectName, ProjectId: projectId,
                    Pipeline: kvp.Value.Pipeline.ToString().ToLowerInvariant(),
                    Title: kvp.Value.Title);
            })
            .ToList();

        if (includeInternal)
        {
            HashSet<string> userKeys = new(threads.Select(t => t.Key), StringComparer.OrdinalIgnoreCase);
            IEnumerable<ThreadEntry> internalThreads = allThreads
                .Where(kvp => !userKeys.Contains(kvp.Key))
                .Select(kvp => new ThreadEntry(kvp.Key, kvp.Value.Pipeline.ToString(), IsInternal: true,
                    kvp.Value.LastMessageAt, kvp.Value.History.Count));
            threads.AddRange(internalThreads);
        }

        return Ok(threads.OrderByDescending(t => t.LastMessageAt).ToList());
    }

    [HttpPost]
    public IActionResult NewThread([FromBody] NewThreadRequest? req = null)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        // Desktop (Electron) clients get a "client-" key so they're distinguishable from browser ("web-") threads.
        string key = $"{(req?.Desktop == true ? "client" : "web")}-{Guid.NewGuid():N}";
        Project? project = !string.IsNullOrWhiteSpace(req?.ProjectId) ? projectStore.Get(req.ProjectId) : null;
        if (project is not null)
            projectStore.BindThread(key, req!.ProjectId!);
        // Pre-register in LLMModule so the newThread event fires immediately and
        // all sidebar observers see the thread without waiting for the first message.
        // An explicit pipeline selection wins; otherwise a Repository project opens in code-mode.
        if (Enum.TryParse(req?.Pipeline, ignoreCase: true, out ARI.LLM.ThreadPipeline selected))
            Llm.ForcePipeline(key, selected);
        else if (project?.RootPath is { } pr && Directory.Exists(Path.Combine(pr, ".git")))
            Llm.ForceCodeThread(key);
        else
            Llm.GetOrCreateDialogueThread(key);
        return Ok(new { key });
    }

    /// <summary>
    /// The pipelines a thread can run on, lowercased (e.g. "dialogue", "code", "speech"). The client
    /// renders its selector from this list and maps each name to a label/icon with a generic fallback,
    /// so adding a ThreadPipeline value surfaces in the UI without a frontend change.
    /// </summary>
    [HttpGet("~/pipelines")]
    public IActionResult GetPipelines()
        => Ok(Enum.GetNames<ARI.LLM.ThreadPipeline>().Select(n => n.ToLowerInvariant()).ToList());

    /// <summary>
    /// Returns thread metadata plus full history. The primary polling endpoint for streaming threads.
    /// Poll at ~150ms while thread.state == "streaming"; stop once it is anything else (active/inactive/…).
    /// DebugRequestJson / DebugResponseText are excluded here — use GET /threads/{key}/debug for those.
    /// </summary>
    [HttpGet("{threadKey}")]
    public IActionResult GetThread(string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        if (!Llm.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? thread))
            return NotFound();

        if (!CanAccessThread(thread)) return Forbid();

        List<ThreadItem> history = thread.History
            .Where(i => i.IsVisible && i is not Response { State: State.Cancelled })
            .ToList();

        return Ok(new
        {
            key           = threadKey,
            state         = thread.State.ToString().ToLowerInvariant(),
            pipeline      = thread.Pipeline.ToString().ToLowerInvariant(),
            isInternal    = thread.Internal,
            lastMessageAt = thread.LastMessageAt,
            history,
        });
    }

    /// <summary>
    /// Returns thread history with DebugRequestJson, DebugResponseText and Reasoning exposed on Response
    /// items, plus any spawned sub-threads (a CodeArchitect's plan + per-task Coder threads) nested under
    /// <c>children</c> so the otherwise-invisible orchestration is fully inspectable. Shape:
    /// <c>{ key, label, isInternal, pipeline, history: [...], children: [ {same shape}, ... ] }</c>.
    /// Used exclusively by the control-panel Debug Threads pane — not for normal clients.
    /// </summary>
    [HttpGet("{threadKey}/debug")]
    public IActionResult GetThreadDebug(string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        if (!Llm.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? thread))
            return NotFound();

        if (!CanAccessThread(thread)) return Forbid();

        return Ok(SerializeDebugThread(thread));
    }

    /// <summary>Lists today's recorded session files from disk. Includes completed Engram sweeps and other
    /// ephemeral threads that are no longer in the live registry. Admin-only (raw session data).</summary>
    [HttpGet("debug/sessions")]
    public IActionResult GetDebugSessions()
    {
        if (!User.IsInRole(ARI.API.Auth.Roles.Admin)) return Forbid();
        var sessions = ARI.LLM.SessionRecorder.ListTodaysSessions();
        return Ok(sessions.Select(s => new { stem = s.Stem, bytes = s.Bytes }));
    }

    /// <summary>Returns the raw events from a disk-recorded session file. Admin-only.</summary>
    [HttpGet("debug/sessions/{stem}")]
    public IActionResult GetDebugSession(string stem)
    {
        if (!User.IsInRole(ARI.API.Auth.Roles.Admin)) return Forbid();
        var lines = ARI.LLM.SessionRecorder.ReadSessionFile(stem);
        if (lines is null) return NotFound();
        return Ok(lines);
    }

    /// <summary>Recursively serialises a thread for the Debug pane: its history (with reasoning + raw
    /// request/response) and every sub-thread it spawned. Debug-only.</summary>
    private static object SerializeDebugThread(ARI.LLM.Thread thread) => new
    {
        key        = thread.Key,
        label      = thread.Label,
        isInternal = thread.Internal,
        pipeline   = thread.Pipeline.ToString().ToLowerInvariant(),
        state      = thread.State.ToString().ToLowerInvariant(),
        history    = thread.History.Select(DebugItem).ToList(),
        children   = thread.Children.Select(SerializeDebugThread).ToList(),
    };

    private static object DebugItem(ThreadItem item)
        => item is Response r
            ? new
            {
                type                      = "ariResponse",
                timestamp                 = r.Timestamp,
                state                     = r.State.ToString().ToLowerInvariant(),
                content                   = r.ContentText,
                isStreaming               = r.IsStreamingJson,
                thinkingSeconds           = r.ThinkingSeconds,
                appraisalGrade            = r.AppraisalGrade,
                appraisalSeconds          = r.AppraisalSeconds,
                recallNotes               = r.RecallNotes,
                contextSummary            = r.ContextSummary,
                completionTokens          = r.Data.CompletionTokens,
                outputTokenLimit          = r.Data.OutputTokenLimit,
                promptTokens              = r.Data.PromptTokens,
                contextTokenLimit         = r.Data.ContextTokenLimit,
                estimatedTextPromptTokens = r.Data.EstimatedTextPromptTokens,
                hadImageAttachments       = r.Data.HadImageAttachments,
                imageTokenLimit           = r.Data.ImageTokenLimit,
                debugRequestJson          = r.Data.DebugRequestJson,
                debugResponseText         = r.Data.DebugResponseText,
                reasoning                 = r.Reasoning,
                trace                     = r.Trace,
            }
            : (object)item;

    [HttpGet("{threadKey}/history")]
    public IActionResult GetHistory(string threadKey, [FromQuery] bool raw = false)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        // Cancelled responses are hidden from normal view; streaming responses are included so
        // watching clients can render the in-progress reply. Raw view keeps everything.
        ARI.LLM.Thread? histThread = raw ? FindAnyThread(threadKey) : FindThread(threadKey);
        if (histThread is not null && !CanAccessThread(histThread)) return Forbid();
        List<ThreadItem> items = raw
            ? (histThread?.History ?? new())
            : (histThread?.History ?? new())
                .Where(i => i.IsVisible && i is not Response { State: State.Cancelled })
                .ToList();

        return Ok(items);
    }

    [HttpGet("{threadKey}/export")]
    public IActionResult ExportLog(string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        ARI.LLM.Thread? exportThread = FindThread(threadKey);
        if (exportThread is not null && !CanAccessThread(exportThread)) return Forbid();
        List<ThreadItem> items = exportThread?.History ?? new();
        string log = string.Join("\n\n", items.Select(i => i.ToString()));
        var bytes = System.Text.Encoding.UTF8.GetBytes(log);
        return File(bytes, "text/plain", $"ari-{threadKey}-{DateTime.Now:yyyyMMdd-HHmm}.txt");
    }

    [HttpGet("{threadKey}/status")]
    public IActionResult GetThreadStatus(string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        return Ok(new { isProcessing = Llm.IsThreadProcessing(threadKey) });
    }

    /// <summary>
    /// Global SSE event stream — one connection per client, covers all threads.
    /// Event types: newThread | streaming | streamingFinished | threadDeleted | threadUpdated
    /// </summary>
    [HttpGet("~/events")]
    public async Task Events(CancellationToken cancellationToken)
    {
        Response.Headers[HeaderNames.ContentType]  = "text/event-stream";
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        Response.Headers["X-Accel-Buffering"]      = "no";

        if (Llm is null)
        {
            await Response.WriteAsync("data: {\"error\":\"not ready\"}\n\n", cancellationToken);
            return;
        }

        bool   evtCallerIsAdmin = User.FindFirstValue(System.Security.Claims.ClaimTypes.Role) == ARI.API.Auth.Roles.Admin;
        string? evtCallerId     = evtCallerIsAdmin ? null : User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);

        Channel<AppEvent> channel = Channel.CreateUnbounded<AppEvent>(new UnboundedChannelOptions { SingleReader = true });
        using IDisposable sub = Llm.Subscribe(channel);

        while (!cancellationToken.IsCancellationRequested)
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            AppEvent? evt;
            try { evt = await channel.Reader.ReadAsync(timeoutCts.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await Response.WriteAsync(": ping\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                continue;
            }

            // Guests only receive events for threads they own.
            if (!evtCallerIsAdmin)
            {
                Llm.Threads.TryGetValue(evt.ThreadKey, out ARI.LLM.Thread? evtThread);
                if (evtThread is null || evtThread.OwnerId != evtCallerId) continue;
            }

            string payload = JsonSerializer.Serialize(evt, SseJson);
            await Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Per-thread SSE endpoint — kept for debug panel / legacy consumers.
    /// </summary>
    [HttpGet("{threadKey}/watch")]
    public async Task Watch(string threadKey, CancellationToken cancellationToken)
    {
        Response.Headers[HeaderNames.ContentType]  = "text/event-stream";
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        Response.Headers["X-Accel-Buffering"]      = "no";

        if (Llm is null)
        {
            await Response.WriteAsync("data: {\"error\":\"not ready\"}\n\n", cancellationToken);
            return;
        }

        if (Llm.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? watchThread) && !CanAccessThread(watchThread))
        {
            Response.StatusCode = 403;
            return;
        }

        Channel<bool?> channel = Channel.CreateUnbounded<bool?>(new UnboundedChannelOptions { SingleReader = true });
        using IDisposable watchHandle = Llm.WatchThread(threadKey, channel);

        // Send initial state so the client can sync immediately on connect.
        await SendWatchEvent(threadKey, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            // Wait up to 20s for an update; send a keep-alive ping either way.
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            bool? signal;
            try
            {
                signal = await channel.Reader.ReadAsync(timeoutCts.Token);
                while (channel.Reader.TryRead(out _)) { }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await Response.WriteAsync(": ping\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                continue;
            }

            if (signal is null)
            {
                await Response.WriteAsync("data: {\"deleted\":true}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                return;
            }

            await SendWatchEvent(threadKey, cancellationToken);
        }
    }

    private async Task SendWatchEvent(string threadKey, CancellationToken ct)
    {
        string status = (Llm?.IsEngramSweeping(threadKey)  ?? false) ? "remembering"
                      : Llm?.GetThreadPhase(threadKey) switch
                        {
                            ARI.LLM.ThreadPhase.Prefilling  => "prefilling",
                            ARI.LLM.ThreadPhase.Thinking    => "thinking",
                            ARI.LLM.ThreadPhase.Typing      => "typing",
                            ARI.LLM.ThreadPhase.Researching => "researching",
                            ARI.LLM.ThreadPhase.Generating  => "generating",
                            _                              => (Llm?.IsThreadProcessing(threadKey) ?? false) ? "prefilling" : "idle",
                        };
        bool isCodeMode = Llm?.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? wt) == true && wt?.Pipeline == ARI.LLM.ThreadPipeline.Code;
        string payload  = JsonSerializer.Serialize(new { status, isCodeMode });
        await Response.WriteAsync($"data: {payload}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    [HttpPost("~/commands")]
    public async Task<IActionResult> RunCommand([FromBody] CommandRequest req)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        if (string.IsNullOrWhiteSpace(req.Input)) return BadRequest("Input is required.");

        string? result = await Llm.HandleCommand(req.ThreadKey, req.Input);
        if (result is null) return BadRequest(new { error = $"Unknown command: {req.Input}" });
        return Ok(new { result });
    }

    // ── Attachments ─────────────────────────────────────────────────────────────

    private static readonly HashSet<string> ImageMimes = new(StringComparer.OrdinalIgnoreCase)
        { "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp" };

    // Mime types that are binary and cannot be read as plain text.
    private static readonly HashSet<string> BinaryMimes = new(StringComparer.OrdinalIgnoreCase)
        { "application/pdf", "application/zip", "application/x-zip-compressed",
          "application/octet-stream", "application/x-rar-compressed", "application/x-7z-compressed",
          "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
          "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
          "application/vnd.openxmlformats-officedocument.presentationml.presentation",
          "application/msword", "application/vnd.ms-excel", "application/vnd.ms-powerpoint" };

    [HttpPost("{threadKey}/attachments")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> AddAttachment(string threadKey, IFormFile file)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        if (file is null || file.Length == 0) return BadRequest("No file provided.");

        string mime = file.ContentType ?? "application/octet-stream";
        bool isZip  = mime is "application/zip" or "application/x-zip-compressed"
                   || file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        // Resolve the target directory — project dir if already bound, otherwise the thread's scratchpad.
        string targetDir = FindThread(threadKey)?.FilesystemRoot is { } root
                        && !root.StartsWith(Paths.ServerDir("Scratchpad"), StringComparison.OrdinalIgnoreCase)
            ? root
            : Paths.ScratchpadDir(threadKey);

        // ── Zip: extract each text file into the target directory ─────────────────
        if (isZip)
        {
            using MemoryStream zipStream = new();
            await file.CopyToAsync(zipStream);
            zipStream.Position = 0;

            List<string> extracted = new();
            List<string> skipped   = new();

            using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (entry.Name.StartsWith('.'))        continue;
                if (IsSkippedZipPath(entry.FullName))  continue;

                string destPath = Path.Combine(targetDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                using FileStream fs = System.IO.File.Create(destPath);
                await entry.Open().CopyToAsync(fs);
                extracted.Add(entry.FullName);
            }

            return Ok(new { zip = file.FileName, extracted, skipped });
        }

        // ── Normal single file ────────────────────────────────────────────────────
        if (BinaryMimes.Contains(mime))
            return StatusCode(415, new { error = $"{Path.GetExtension(file.FileName).TrimStart('.').ToUpper()} files cannot be attached — binary formats are not supported." });

        // All files land on the filesystem (scratchpad or project dir); ARI reads them via read_file.
        Directory.CreateDirectory(targetDir);
        string fileDest = Path.Combine(targetDir, file.FileName);
        await using FileStream destFs = System.IO.File.Create(fileDest);
        await file.CopyToAsync(destFs);
        pendingFileNotes.GetOrAdd(threadKey, _ => new()).Add(file.FileName);

        return Ok(new { name = file.FileName });
    }

    /// <summary>Extensions treated as plain text and extracted from zips.</summary>
    private static bool IsTextExtension(string ext) => ext is
        ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".json" or ".xml" or ".yaml" or ".yml"
        or ".md" or ".txt" or ".html" or ".css" or ".scss" or ".less" or ".razor" or ".cshtml"
        or ".py" or ".go" or ".rs" or ".cpp" or ".c" or ".h" or ".java" or ".kt" or ".swift"
        or ".sh" or ".bash" or ".ps1" or ".toml" or ".ini" or ".env" or ".config" or ".csproj"
        or ".sln" or ".props" or ".targets" or ".sql" or ".graphql" or ".proto";

    /// <summary>Zip paths to silently skip (build output, deps, hidden dirs).</summary>
    private static bool IsSkippedZipPath(string fullName) =>
        fullName.Contains("/bin/",        StringComparison.OrdinalIgnoreCase) ||
        fullName.Contains("/obj/",        StringComparison.OrdinalIgnoreCase) ||
        fullName.Contains("/node_modules/",StringComparison.OrdinalIgnoreCase) ||
        fullName.Contains("/.git/",       StringComparison.OrdinalIgnoreCase) ||
        fullName.Contains("/.vs/",        StringComparison.OrdinalIgnoreCase) ||
        fullName.StartsWith("__MACOSX/",  StringComparison.OrdinalIgnoreCase);

    [HttpDelete("{threadKey}/attachments/{name}")]
    public IActionResult RemoveAttachment(string threadKey, string name)
    {
        // Remove from pending message attachments if present (per-message images staged before send).
        if (pendingMessageAttachments.TryGetValue(threadKey, out List<Attachment>? msgList))
            msgList.RemoveAll(a => a.Name == name);

        // Remove from disk if present.
        string fileRoot = FindThread(threadKey)?.FilesystemRoot ?? Paths.ScratchpadDir(threadKey);
        string path     = Path.Combine(fileRoot, name);
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);

        return Ok();
    }

    [HttpGet("{threadKey}/attachments")]
    public IActionResult GetAttachments(string threadKey)
    {
        static bool IsImageExt(string ext) => ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

        // All thread files live on disk (scratchpad or project dir).
        string scratchpad = Paths.ScratchpadDir(threadKey);
        string fileRoot   = FindThread(threadKey)?.FilesystemRoot ?? scratchpad;
        bool   isInScratchpad = fileRoot.StartsWith(Path.GetFullPath(scratchpad), StringComparison.OrdinalIgnoreCase);

        IEnumerable<object> diskFiles = Directory.Exists(fileRoot)
            ? Directory.GetFiles(fileRoot, "*", SearchOption.AllDirectories)
                       .Select(p =>
                       {
                           string rel = Path.GetRelativePath(fileRoot, p);
                           string ext = Path.GetExtension(p).ToLowerInvariant();
                           bool isImg = IsImageExt(ext);
                           // Scratchpad files are served via /scratchpad/{name}; project files via /file?path=...
                           string url = isInScratchpad && !rel.Contains(Path.DirectorySeparatorChar)
                               ? $"/threads/{Uri.EscapeDataString(threadKey)}/scratchpad/{Uri.EscapeDataString(rel)}"
                               : $"/threads/{Uri.EscapeDataString(threadKey)}/file?path={Uri.EscapeDataString(rel)}";
                           return (object)new { Name = rel, Url = url, IsImage = isImg };
                       })
            : Enumerable.Empty<object>();

        return Ok(diskFiles);
    }

    // ── Promote scratchpad to project ──────────────────────────────────────────

    [HttpPost("{threadKey}/promote-to-project")]
    public IActionResult PromoteToProject(string threadKey, [FromBody] PromoteToProjectRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Name))
            return BadRequest(new { error = "Name is required." });

        string scratchpadDir = Paths.ScratchpadDir(threadKey);
        ARI.LLM.Thread? thread = FindThread(threadKey);
        string? currentRoot = thread?.FilesystemRoot;

        // Only scratchpad-rooted threads can be promoted (not already-bound projects).
        if (currentRoot is null || !currentRoot.StartsWith(scratchpadDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(scratchpadDir))
                return BadRequest(new { error = "No scratchpad to promote." });
        }

        if (Modules.Projects is not { } svc)
            return StatusCode(503, new { error = "Project service not available." });

        // Create the project record (ServerFs — destination is on this server).
        ProjectSummary? summary = svc.Create(req.Name.Trim(), req.Category, "ServerFs");
        if (summary is null) return StatusCode(500, new { error = "Failed to create project record." });

        Project? project = projectStore.Get(summary.Id);
        if (project?.RootPath is not { } destDir)
            return StatusCode(500, new { error = "Project has no server root." });

        // Move files from scratchpad into the new project dir.
        if (Directory.Exists(scratchpadDir))
        {
            // If CreateServerFolder already created the destination, remove it so Move can replace it.
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
            Directory.Move(scratchpadDir, destDir);
        }

        // Bind the thread to the new project so file tools continue working.
        svc.BindThread(threadKey, summary.Id);

        return Ok(new { projectId = summary.Id, name = summary.Name, rootPath = destDir });
    }

    // ── Message Attachments (ephemeral — cleared after send) ────────────────────

    [HttpPost("{threadKey}/message-attachments")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> AddMessageAttachment(string threadKey, IFormFile file)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        if (file is null || file.Length == 0) return BadRequest("No file provided.");

        string mime    = file.ContentType ?? "application/octet-stream";
        bool   isImage = ImageMimes.Contains(mime);

        if (!isImage && BinaryMimes.Contains(mime))
            return StatusCode(415, new { error = $"{Path.GetExtension(file.FileName).TrimStart('.').ToUpper()} files are not supported — only images and plain text files can be attached to a message." });

        string content;
        if (isImage)
        {
            using MemoryStream ms = new();
            await file.CopyToAsync(ms);
            content = Convert.ToBase64String(ms.ToArray());
        }
        else
        {
            using System.IO.StreamReader reader = new(file.OpenReadStream());
            content = await reader.ReadToEndAsync();
        }

        // Large text files are promoted to the thread scratchpad instead of being inlined.
        if (!isImage && content.Length > 10_000)
        {
            string destPath = Path.Combine(Paths.ScratchpadDir(threadKey), file.FileName);
            await System.IO.File.WriteAllTextAsync(destPath, content);
            return Ok(new { name = file.FileName, isImage = false, mimeType = mime, promoted = true });
        }

        // Images are also saved to the scratchpad so ARI can pass them to image generation tools.
        if (isImage)
        {
            string scratchDir = Paths.ScratchpadDir(threadKey);
            Directory.CreateDirectory(scratchDir);
            byte[] imgBytes = Convert.FromBase64String(content);
            await System.IO.File.WriteAllBytesAsync(Path.Combine(scratchDir, file.FileName), imgBytes);
        }

        Attachment attachment = new Attachment { Name = file.FileName, Content = content, IsImage = isImage, MimeType = mime };
        List<Attachment> msgList = pendingMessageAttachments.GetOrAdd(threadKey, _ => new());
        msgList.RemoveAll(a => a.Name == attachment.Name);
        msgList.Add(attachment);
        return Ok(new { name = file.FileName, isImage, mimeType = mime, promoted = false });
    }

    [HttpDelete("{threadKey}/message-attachments/{name}")]
    public IActionResult RemoveMessageAttachment(string threadKey, string name)
    {
        if (pendingMessageAttachments.TryGetValue(threadKey, out List<Attachment>? list))
            list.RemoveAll(a => a.Name == name);
        return Ok();
    }

    [HttpGet("{threadKey}/message-attachments")]
    public IActionResult GetMessageAttachments(string threadKey)
    {
        List<Attachment> staged = pendingMessageAttachments.TryGetValue(threadKey, out List<Attachment>? list) ? list : new();
        return Ok(staged.Select(a => new { a.Name, a.IsImage, a.MimeType, a.Content }));
    }

    /// <summary>
    /// Serves the raw content of an attachment that was sent with a message.
    /// Images are returned as their native mime type; text files as plain text.
    /// Identified by the message timestamp and filename since content is stripped from history JSON.
    /// </summary>
    [HttpGet("{threadKey}/msg-attachment")]
    public IActionResult GetMessageAttachmentContent(string threadKey, [FromQuery] string name)
    {
        if (Llm is null) return StatusCode(503);

        List<ThreadItem> items = FindThread(threadKey)?.History ?? new();
        foreach (ThreadItem item in items)
        {
            if (item is not Prompt msg || msg.Attachments is null) continue;
            Attachment? att = msg.Attachments.FirstOrDefault(a => a.Name == name);
            if (att is null) continue;

            if (att.IsImage)
            {
                byte[] bytes = Convert.FromBase64String(att.Content);
                return File(bytes, att.MimeType ?? "image/jpeg");
            }
            return Content(att.Content, "text/plain");
        }

        return NotFound();
    }

    /// <summary>
    /// Serves a file ARI delivered (via the deliver_file tool) as a download. The file lives in the
    /// thread's file root — its bound project dir or its scratchpad — and is resolved by the relative
    /// path from the download card. Path traversal outside the root is refused.
    /// </summary>
    [HttpGet("{threadKey}/file")]
    public IActionResult GetDeliveredFile(string threadKey, [FromQuery] string path)
    {
        if (Llm is null) return StatusCode(503);
        if (string.IsNullOrWhiteSpace(path)) return BadRequest("path is required.");

        string root    = FindThread(threadKey)?.FilesystemRoot ?? Paths.ScratchpadDir(threadKey);
        string rootAbs = Path.GetFullPath(root);
        string absPath = Path.GetFullPath(Path.Combine(rootAbs, path));
        if (!absPath.StartsWith(rootAbs, StringComparison.OrdinalIgnoreCase))
            return BadRequest("Access denied.");
        if (!System.IO.File.Exists(absPath)) return NotFound();

        return PhysicalFile(absPath, "application/octet-stream", Path.GetFileName(absPath));
    }

    /// <summary>
    /// Serves a file from a thread's scratchpad for inline display (images, etc.).
    /// Only files directly inside the scratchpad root are accessible — no subdirectory traversal.
    /// </summary>
    [HttpGet("{threadKey}/scratchpad/{filename}")]
    public IActionResult GetScratchpadFile(string threadKey, string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.Contains('/') || filename.Contains('\\'))
            return BadRequest("Invalid filename.");

        string dir     = Paths.ScratchpadDir(threadKey);
        string absPath = Path.Combine(Path.GetFullPath(dir), filename);

        // Verify the resolved path is still inside the scratchpad dir (belt-and-suspenders).
        if (!absPath.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
            return BadRequest("Access denied.");

        if (!System.IO.File.Exists(absPath)) return NotFound();

        string ext  = Path.GetExtension(filename).TrimStart('.').ToLowerInvariant();
        string mime = ext switch { "png" => "image/png", "jpg" or "jpeg" => "image/jpeg", "gif" => "image/gif", "webp" => "image/webp", "mp4" => "video/mp4", "webm" => "video/webm", _ => "application/octet-stream" };
        return PhysicalFile(absPath, mime);
    }

    /// <summary>
    /// Heartbeat sent by the web client while the user is actively composing a message.
    /// Resets the thread's inactivity countdown so Engram doesn't sweep mid-composition.
    /// </summary>
    /// <summary>
    /// Injects a named text attachment into an existing thread (or stages it if the thread
    /// hasn't been created yet). Used by the Electron client to supply the project file tree
    /// and file read results without routing through a user message.
    /// </summary>
    [HttpPost("{threadKey}/system-context")]
    public IActionResult SetSystemContext(string threadKey, [FromBody] InjectContextRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Content is null)
            return BadRequest("Name and Content are required.");
        pendingSystemContext[threadKey] = req.Content;
        return Ok();
    }

    [HttpPost("{threadKey}/typing")]
    public IActionResult NotifyTyping(string threadKey)
    {
        Llm?.NotifyTyping(threadKey);
        return Ok();
    }

    [HttpDelete("{threadKey}/processing")]
    public IActionResult CancelProcessing(string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        Llm.Cancel(threadKey);
        return Ok();
    }

    /// <summary>Esc = stop. Cancels the in-flight turn but PRESERVES the partial work (thinking + partial
    /// reply/tools) so nothing is lost — the user's next message is a fresh turn.</summary>
    [HttpPost("{threadKey}/interrupt")]
    public IActionResult InterruptProcessing(string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        Llm.Interrupt(threadKey);
        return Ok();
    }

    /// <summary>Mid-turn message = "stop and read this, then continue". The turn keeps running; the message is
    /// folded into Ari's current chain of thought. Returns 409 if the thread isn't streaming (send normally).</summary>
    [HttpPost("{threadKey}/interject")]
    public IActionResult Interject(string threadKey, [FromBody] InterjectRequest body)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        bool folded = Llm.Interject(threadKey, GetUsername(), body?.Text ?? "");
        return folded ? Ok() : Conflict("Thread is not streaming.");
    }

    /// <summary>Close a thread: runs Engram to save it to memory, then deletes it. Fires a threadDeleted event.</summary>
    [HttpDelete("{threadKey}")]
    public async Task<IActionResult> CloseThread(string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        bool closed = await Llm.CloseThreadAsync(threadKey);
        return closed ? Ok() : NotFound();
    }

    [HttpPost("{threadKey}/stream")]
    public async Task Stream(string threadKey, [FromBody] StreamRequest body, CancellationToken cancellationToken)
    {
        string prompt = body?.Prompt ?? string.Empty;
        // Safe mode has two halves: the persistent thread flag (enforced as a hard edit-block in the Coder's
        // tool layer) and the prompt injection that steers her toward read-only planning. Set both. The flag
        // mirrors the toggle each send; CodePipeline clears it when the user approves a plan.
        Llm?.SetSafeMode(threadKey, body?.SafeMode == true);
        if (body?.SafeMode == true)
            prompt = string.IsNullOrWhiteSpace(prompt)
                ? SafeModePromptStore.Get()
                : $"{prompt}\n\n{SafeModePromptStore.Get()}";
        Response.Headers[HeaderNames.ContentType] = "text/event-stream";
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        if (Llm is null)
        {
            await Response.WriteAsync("data: [ERROR] ARI is not ready yet.\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        // A prompt sent while the model servers are still booting is QUEUED, not rejected (#77): hold it
        // here until every boot-startup server reports Online (the client's typing indicator covers the
        // wait), and only error out if boot genuinely never completes. Prompting mid-boot used to surface
        // raw connection errors to the user.
        // Nothing online at all and nothing coming: the user has not started a server yet (a fresh
        // install ships one that does not boot on startup). Say so plainly instead of waiting four
        // minutes for a boot that was never going to happen, or handing back a raw connection error.
        if (!Llm.Servers.Any(s => s.Status == ARI.LLM.ServerStatus.Online)
            && !Llm.Servers.Any(s => s.BootStartup || s.Status == ARI.LLM.ServerStatus.Starting))
        {
            await Response.WriteAsync("data: [ERROR] No model server is running. Open the control panel, choose the model you want, and start a server.\n\n", cancellationToken);
            await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        if (Llm.Servers.Any(s => s.BootStartup && s.Status != ARI.LLM.ServerStatus.Online))
        {
            DateTime bootDeadline = DateTime.UtcNow.AddMinutes(4);
            while (DateTime.UtcNow < bootDeadline
                   && Llm.Servers.Any(s => s.BootStartup && s.Status != ARI.LLM.ServerStatus.Online))
                await Task.Delay(1000, cancellationToken);
            if (Llm.Servers.Any(s => s.BootStartup && s.Status != ARI.LLM.ServerStatus.Online))
            {
                await Response.WriteAsync("data: [ERROR] ARI's model server has not come online — please try again in a moment.\n\n", cancellationToken);
                await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                return;
            }
        }

        // Safeguard: reject prompts that are clearly too large for the context window.
        // Estimate at 4 chars/token; limit comes from the thread's configured BudgetContext (0 = unconfigured).
        (int _, int contextLimit) = Llm.GetContextStats(threadKey);
        int effectiveLimit    = contextLimit > 0 ? contextLimit : 8000;
        int estimatedTokens   = prompt.Length / 4;
        if (estimatedTokens > effectiveLimit)
        {
            await Response.WriteAsync("data: Your message is too large for me to process. Please attach the content as a file instead.\n\n", cancellationToken);
            await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        pendingMessageAttachments.TryRemove(threadKey, out List<Attachment>? msgAtts);
        pendingFileNotes.TryRemove(threadKey, out List<string>? fileNotes);
        pendingSystemContext.TryRemove(threadKey, out string? systemContextBlock);

        // ── Scratchpad wiring ─────────────────────────────────────────────────────
        // If files have been written to this thread's scratchpad, bind it as the
        // FilesystemRoot (if no project is already bound) and inject a file listing so
        // ARI knows to use her file tools.
        string? platformContext = null;
        {
            string scratchpadDir = Paths.ScratchpadDir(threadKey);
            ARI.LLM.Thread? scratchThread = FindThread(threadKey);
            bool hasScratchpad = Directory.Exists(scratchpadDir) && Directory.GetFiles(scratchpadDir, "*", SearchOption.AllDirectories).Length > 0;
            bool noProjectBound = !ThreadProjects.ContainsKey(threadKey);

            if (hasScratchpad && noProjectBound && scratchThread is not null && scratchThread.FilesystemRoot is null)
            {
                scratchThread.FilesystemRoot = scratchpadDir;
                scratchThread.IsBrainVault = false;
                scratchThread.Ct = CancellationToken.None;

                string[] files = Directory.GetFiles(scratchpadDir, "*", SearchOption.AllDirectories);
                var listing = new System.Text.StringBuilder();
                listing.AppendLine("[Workspace files]");
                foreach (string f in files)
                    listing.AppendLine(Path.GetRelativePath(scratchpadDir, f));
                listing.AppendLine("Use list_directory and read_file to inspect these files.");
                platformContext = listing.ToString().TrimEnd();
            }
        }

        if (ThreadProjects.TryGetValue(threadKey, out string? pid))
        {
            Project? project = projectStore.Get(pid);
            if (project is not null)
            {
                var ctx = new System.Text.StringBuilder();
                ctx.AppendLine($"Project: {project.Name}");
                if (!string.IsNullOrWhiteSpace(project.Instructions))
                    ctx.AppendLine().AppendLine(project.Instructions);

                // Filesystem skeleton sent via /system-context — lives in the system prompt (stable
                // cached prefix) and never appears as a user-visible thread attachment.
                if (systemContextBlock is not null)
                {
                    ctx.AppendLine().AppendLine(systemContextBlock);
                    ctx.AppendLine("Use list_directory to explore subdirectories and read_file to read files.");
                }

                // If the project folder contains inner git repos, tell ARI to pull before working.
                if (project.RootPath is { } root && Directory.Exists(root))
                {
                    bool hasInnerRepos = Directory.EnumerateDirectories(root)
                        .Any(d => Directory.Exists(Path.Combine(d, ".git")));
                    if (hasInnerRepos)
                        ctx.AppendLine()
                           .AppendLine("This project contains git repositories. Load project_git_tools to get the `git` tool, which lists the available repos automatically. Before working in any repo, run git({repo}, \"fetch\") then git({repo}, \"status\") to check for upstream changes, and git({repo}, \"pull\") to update. Always work on up-to-date code.");
                }

                platformContext = ctx.ToString().TrimEnd();

                ARI.LLM.Thread? boundThread = FindThread(threadKey);

                // Force code pipeline before the classifier runs (first message only)
                bool isFirstMessage = boundThread?.History.Count is null or 0;
                if (isFirstMessage && project.RootPath is { } gitRoot && Directory.Exists(Path.Combine(gitRoot, ".git")))
                    Llm.ForceCodeThread(threadKey);

                // Bind FilesystemRoot on the thread every message (idempotent) so filesystem_tools/coding_tools
                // resolve correctly. Local path (Electron) is preferred and set later via effectiveLocalPath;
                // RootPath here is the server-side fallback for web sessions.
                if (boundThread is not null && project is { RootPath: { } serverRoot })
                {
                    boundThread.FilesystemRoot   = serverRoot;
                    boundThread.IsBrainVault  = false;
                    boundThread.Ct            = CancellationToken.None;
                }

            }
        }

        // Append file-upload notes to platformContext so ARI knows to read them.
        if (fileNotes is { Count: > 0 })
        {
            string noteLines = fileNotes.Count == 1
                ? $"[{fileNotes[0]} was uploaded to your workspace — use read_file to inspect it.]"
                : "[The following files were uploaded to your workspace — use read_file to inspect them: "
                  + string.Join(", ", fileNotes) + "]";
            platformContext = string.IsNullOrEmpty(platformContext)
                ? noteLines
                : platformContext + "\n" + noteLines;
        }

        // Heartbeat: while the model processes (prompt-processing a large context, running tools, thinking) no
        // content deltas are produced, so the SSE connection sits idle. Reverse proxies/tunnels sitting in
        // front of ARI often cap idle connections (commonly ~100s) and cut it, showing "[connection error]".
        // An SSE comment line (":") every 15s keeps the connection warm; the client ignores comment lines. All writes to the response
        // body are serialised through writeLock so the heartbeat and the content callback never issue
        // concurrent writes (which would corrupt the stream).
        using SemaphoreSlim writeLock = new(1, 1);
        using CancellationTokenSource heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task WriteEventAsync(string payload)
        {
            // Use None so a client disconnect doesn't throw — LLM must keep running.
            await writeLock.WaitAsync(CancellationToken.None);
            try
            {
                if (cancellationToken.IsCancellationRequested) return;
                await Response.WriteAsync(payload, CancellationToken.None);
                await Response.Body.FlushAsync(CancellationToken.None);
            }
            catch { /* swallow broken pipe — client navigated away */ }
            finally { writeLock.Release(); }
        }

        Task heartbeat = Task.Run(async () =>
        {
            try
            {
                while (!heartbeatCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), heartbeatCts.Token);
                    await WriteEventAsync(": keepalive\n\n");
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch { /* a broken pipe here is harmless — the main path reports the error */ }
        });

        try
        {
            string username = GetUsername();
            // Pass CancellationToken.None so HTTP client disconnect does NOT cancel the LLM.
            // The LLM runs to completion; explicit cancel via DELETE /processing still works
            // because LLMModule.Cancel() cancels the thread's internal CTS directly.
            // A ServerFs project's root is server-managed — resolve it from the bound project when the
            // client didn't send one explicitly (RemoteFs keeps relying on the client-sent LocalPath,
            // sourced from the desktop app's own per-device folder store, same as before).
            string? effectiveLocalPath = string.IsNullOrWhiteSpace(body.LocalPath) ? null : body.LocalPath;
            if (effectiveLocalPath is null
                && ThreadProjects.TryGetValue(threadKey, out string? boundProjectId)
                && projectStore.Get(boundProjectId) is { RootPath: { } rootPath })
                effectiveLocalPath = rootPath;

            await Llm.PromptStreaming(threadKey, prompt, username, platformContext, async accumulated =>
            {
                string escaped = accumulated.Replace("\n", "\\n").Replace("\r", "");
                await WriteEventAsync($"data: {escaped}\n\n");
            }, CancellationToken.None, messageAttachments: msgAtts,
               localPath: effectiveLocalPath);
            await WriteEventAsync("data: [DONE]\n\n");
        }
        catch (OperationCanceledException)
        {
            await WriteEventAsync("data: [CANCELLED]\n\n");
        }
        catch (Exception ex)
        {
            await WriteEventAsync($"data: [ERROR] {ex.Message}\n\n");
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat; } catch { /* ignore */ }
        }
    }
}

public record StreamRequest(string Prompt, string? LocalPath = null, bool SafeMode = false);
public record InterjectRequest(string? Text);
public record PromoteToProjectRequest(string Name, string? Category = null);
public record CommandRequest(string? ThreadKey, string Input);
public record NewThreadRequest(string? ProjectId, bool Desktop = false, string? Pipeline = null);
public record InjectContextRequest(string Name, string Content);
public record ThreadEntry(string Key, string? AgentName, bool IsInternal, DateTime LastMessageAt, int MessageCount, string State = "active", bool IsCodeMode = false, string? ProjectName = null, string? ProjectId = null, string Pipeline = "dialogue", string? Title = null);

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

    // Maps threadKey → projectId — backed by a persistent JSON file so it survives rebuilds
    private static ConcurrentDictionary<string, string>? _threadProjects;
    private static ConcurrentDictionary<string, string> GetThreadProjects(ProjectStore store)
    {
        if (_threadProjects is not null) return _threadProjects;
        _threadProjects = new ConcurrentDictionary<string, string>(store.LoadThreadMap());
        return _threadProjects;
    }
    private ConcurrentDictionary<string, string> ThreadProjects => GetThreadProjects(projectStore);
    private void PersistThreadProjects() => projectStore.SaveThreadMap(new Dictionary<string, string>(ThreadProjects));

    // Pending attachments staged before a thread exists — flushed at send time.
    private static readonly ConcurrentDictionary<string, List<Attachment>> pendingAttachments        = new();
    private static readonly ConcurrentDictionary<string, List<Attachment>> pendingMessageAttachments = new();

    /// <summary>Derives a display username from the authenticated user's email (strips @domain).</summary>
    private string GetUsername()
    {
        string? email = User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrEmpty(email)) return "xywren";
        int at = email.IndexOf('@');
        string raw = at > 0 ? email[..at] : email;
        // Capitalise first letter for display (xywren → Xywren)
        return raw.Length > 0 ? char.ToUpper(raw[0]) + raw[1..] : raw;
    }

    // ── Thread navigation helpers ───────────────────────────────────────────────

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
        return isCode ? Llm.GetOrCreateCodeThread(threadKey) : Llm.GetOrCreateDialogueThread(threadKey);
    }

    // ── Thread endpoints ────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult GetThreads([FromQuery] bool includeInternal = false)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        var allThreads = Llm.Threads;

        List<ThreadEntry> threads = allThreads
            .Where(kvp => kvp.Value.Pipeline is ARI.LLM.ThreadPipeline.Dialogue or ARI.LLM.ThreadPipeline.Code or ARI.LLM.ThreadPipeline.Speech)
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
        {
            ThreadProjects[key] = req!.ProjectId!;
            PersistThreadProjects();
        }
        // Pre-register in LLMModule so the newThread event fires immediately and
        // all sidebar observers see the thread without waiting for the first message.
        // An explicit pipeline selection wins; otherwise projects with ForceCodePipeline open in code-mode.
        if (Enum.TryParse(req?.Pipeline, ignoreCase: true, out ARI.LLM.ThreadPipeline selected))
            Llm.ForcePipeline(key, selected);
        else if (project?.ForceCodePipeline == true)
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
    /// Poll at ~150ms while thread.state == "streaming"; stop on "idle" or "dormant".
    /// DebugRequestJson / DebugResponseText are excluded here — use GET /threads/{key}/debug for those.
    /// </summary>
    [HttpGet("{threadKey}")]
    public IActionResult GetThread(string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        if (!Llm.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? thread))
            return NotFound();

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

        return Ok(SerializeDebugThread(thread));
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
        List<ThreadItem> items = raw
            ? FindAnyThread(threadKey)?.History ?? new()
            : (FindThread(threadKey)?.History ?? new())
                .Where(i => i.IsVisible && i is not Response { State: State.Cancelled })
                .ToList();

        return Ok(items);
    }

    [HttpGet("{threadKey}/export")]
    public IActionResult ExportLog(string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        List<ThreadItem> items = FindThread(threadKey)?.History ?? new();
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
        bool isProcessing  = Llm?.IsThreadProcessing(threadKey)  ?? false;
        bool isRemembering = Llm?.IsEngramSweeping(threadKey)     ?? false;
        bool isCodeMode    = Llm?.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? wt) == true && wt?.Pipeline == ARI.LLM.ThreadPipeline.Code;
        string payload     = JsonSerializer.Serialize(new { isProcessing, isRemembering, isCodeMode });
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

        // ── Zip: extract and add each text file as a separate attachment ──────────
        if (isZip)
        {
            using MemoryStream zipStream = new();
            await file.CopyToAsync(zipStream);
            zipStream.Position = 0;

            List<string> extracted  = new();
            List<string> skipped    = new();

            using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                // Skip directories, hidden files, and binary/build artefacts.
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (entry.Name.StartsWith('.'))        continue;
                if (IsSkippedZipPath(entry.FullName))  continue;

                string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (!IsTextExtension(ext))
                {
                    skipped.Add(entry.FullName);
                    continue;
                }

                using StreamReader reader  = new(entry.Open());
                string             content = await reader.ReadToEndAsync();

                Attachment att = new() { Name = entry.FullName, Content = content, IsImage = false, MimeType = "text/plain" };
                pendingAttachments.GetOrAdd(threadKey, _ => new()).RemoveAll(a => a.Name == att.Name);
                pendingAttachments[threadKey].Add(att);
                extracted.Add(entry.FullName);
            }

            return Ok(new { zip = file.FileName, extracted, skipped });
        }

        // ── Normal single file ────────────────────────────────────────────────────
        bool isImage = ImageMimes.Contains(mime);

        if (!isImage && BinaryMimes.Contains(mime))
            return StatusCode(415, new { error = $"{Path.GetExtension(file.FileName).TrimStart('.').ToUpper()} files cannot be attached as thread context — only images and text files are supported." });

        string fileContent;
        if (isImage)
        {
            using MemoryStream ms = new();
            await file.CopyToAsync(ms);
            fileContent = Convert.ToBase64String(ms.ToArray());
        }
        else
        {
            using System.IO.StreamReader reader = new(file.OpenReadStream());
            fileContent = await reader.ReadToEndAsync();
        }

        Attachment attachment = new() { Name = file.FileName, Content = fileContent, IsImage = isImage, MimeType = mime };
        List<Attachment> list = pendingAttachments.GetOrAdd(threadKey, _ => new());
        list.RemoveAll(a => a.Name == attachment.Name);
        list.Add(attachment);
        return Ok(new { name = file.FileName, isImage });
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
        if (pendingAttachments.TryGetValue(threadKey, out List<Attachment>? list))
            list.RemoveAll(a => a.Name == name);
        FindThread(threadKey)?.RemoveAttachment(name);
        return Ok();
    }

    [HttpGet("{threadKey}/attachments")]
    public IActionResult GetAttachments(string threadKey)
    {
        List<Attachment> staged  = pendingAttachments.TryGetValue(threadKey, out List<Attachment>? list) ? list : new();
        List<Attachment> onThread = FindThread(threadKey)?.GetAttachments().ToList() ?? new();
        IEnumerable<Attachment> all = staged.Concat(onThread.Where(a => staged.All(s => s.Name != a.Name)));
        return Ok(all.Select(a => new { a.Name, a.IsImage, a.MimeType }));
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

        Attachment attachment = new Attachment { Name = file.FileName, Content = content, IsImage = isImage, MimeType = mime };
        List<Attachment> msgList = pendingMessageAttachments.GetOrAdd(threadKey, _ => new());
        msgList.RemoveAll(a => a.Name == attachment.Name);
        msgList.Add(attachment);
        return Ok(new { name = file.FileName, isImage, mimeType = mime, content });
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
    /// Heartbeat sent by the web client while the user is actively composing a message.
    /// Resets the thread's inactivity countdown so Engram doesn't sweep mid-composition.
    /// </summary>
    /// <summary>
    /// Injects a named text attachment into an existing thread (or stages it if the thread
    /// hasn't been created yet). Used by the Electron client to supply the project file tree
    /// and file read results without routing through a user message.
    /// </summary>
    [HttpPost("{threadKey}/inject-context")]
    public IActionResult InjectContext(string threadKey, [FromBody] InjectContextRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Content is null)
            return BadRequest("Name and Content are required.");

        Attachment att = new() { Name = req.Name, Content = req.Content, IsImage = false, MimeType = "text/plain" };

        // If the thread already exists in an agent, add directly so it persists permanently
        ARI.LLM.Thread? thread = FindThread(threadKey);
        if (thread is not null)
        {
            thread.AddAttachment(att);
        }
        else
        {
            // Thread not yet initialised — stage in pending attachments (flushed on first send)
            pendingAttachments.GetOrAdd(threadKey, _ => new()).RemoveAll(a => a.Name == att.Name);
            pendingAttachments[threadKey].Add(att);
        }

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
        pendingAttachments.TryRemove(threadKey, out List<Attachment>? threadAtts);

        string? platformContext = null;
        if (ThreadProjects.TryGetValue(threadKey, out string? pid))
        {
            Project? project = projectStore.Get(pid);
            if (project is not null)
            {
                var ctx = new System.Text.StringBuilder();
                ctx.AppendLine($"Project: {project.Name}");
                if (!string.IsNullOrWhiteSpace(project.Instructions))
                    ctx.AppendLine().AppendLine(project.Instructions);
                // If the client injected a file tree for this thread, tell the LLM about it
                bool hasTree = threadAtts?.Any(a => a.Name == "_project_tree.txt") == true;
                if (hasTree)
                {
                    ctx.AppendLine().AppendLine("The complete project file tree is provided in the context attachment `_project_tree.txt`. Use the `read_file` tool to read specific files whenever you need to examine their contents.");
                }
                platformContext = ctx.ToString().TrimEnd();

                // Force code pipeline before the classifier runs (first message only)
                bool isFirstMessage = FindThread(threadKey)?.History.Count is null or 0;
                if (isFirstMessage && project.ForceCodePipeline)
                    Llm.ForceCodeThread(threadKey);

                // On the first message, inject project-level attachments as thread attachments
                if (isFirstMessage)
                {
                    List<string> attachmentNames = projectStore.GetAttachmentNames(pid);
                    if (attachmentNames.Count > 0)
                    {
                        threadAtts ??= new();
                        foreach (string name in attachmentNames)
                        {
                            byte[]? data = projectStore.ReadAttachment(pid, name);
                            if (data is null) continue;
                            string ext  = Path.GetExtension(name).ToLowerInvariant();
                            bool isImg  = ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp";
                            string mime = isImg ? $"image/{ext.TrimStart('.')}" : "text/plain";
                            string content = isImg
                                ? Convert.ToBase64String(data)
                                : System.Text.Encoding.UTF8.GetString(data);
                            threadAtts.Add(new Attachment { Name = name, Content = content, IsImage = isImg, MimeType = mime });
                        }
                    }
                }
            }
        }

        // Heartbeat: while the model processes (prompt-processing a large context, running tools, thinking) no
        // content deltas are produced, so the SSE connection sits idle. Proxies/tunnels (e.g. Cloudflare's
        // ~100s idle cap) then cut it and the client shows "[connection error]". An SSE comment line (":")
        // every 15s keeps the connection warm; the client ignores comment lines. All writes to the response
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
            await Llm.PromptStreaming(threadKey, prompt, username, platformContext, async accumulated =>
            {
                string escaped = accumulated.Replace("\n", "\\n").Replace("\r", "");
                await WriteEventAsync($"data: {escaped}\n\n");
            }, CancellationToken.None, messageAttachments: msgAtts, threadAttachments: threadAtts,
               localPath: string.IsNullOrWhiteSpace(body.LocalPath) ? null : body.LocalPath);
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

public record StreamRequest(string Prompt, string? LocalPath = null);
public record CommandRequest(string? ThreadKey, string Input);
public record NewThreadRequest(string? ProjectId, bool Desktop = false, string? Pipeline = null);
public record InjectContextRequest(string Name, string Content);
public record ThreadEntry(string Key, string? AgentName, bool IsInternal, DateTime LastMessageAt, int MessageCount, string State = "active", bool IsCodeMode = false, string? ProjectName = null, string? ProjectId = null, string Pipeline = "dialogue", string? Title = null);

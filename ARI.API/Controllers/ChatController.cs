using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ARI.API;
using ARI.LLM;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ARI.API.Controllers;


[Route("api/threads")]
[ApiController]
public class ThreadsController(LlmServiceHolder holder, ProjectStore projectStore) : ControllerBase
{
    private LlmService? Llm => holder.Service;

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
    private ARI.LLM.Thread? GetThread(string threadKey)
        => Llm?.Agents.GetValueOrDefault("Dialogue")?.GetThread(threadKey)
        ?? Llm?.Agents.GetValueOrDefault("Code")?.GetThread(threadKey);

    /// <summary>Finds an existing internal agent thread (Engram, Refactor, etc.) by key.</summary>
    private ARI.LLM.Thread? GetInternalThread(string threadKey)
    {
        foreach (string name in new[] { "Engram", "Refactor", "Context", "Memory" })
        {
            ARI.LLM.Thread? t = Llm?.Agents.GetValueOrDefault(name)?.GetThread(threadKey);
            if (t is not null) return t;
        }
        return null;
    }

    /// <summary>Gets or creates the correct thread for the given key, routing to Code or Dialogue.</summary>
    private ARI.LLM.Thread GetOrCreateThread(string threadKey)
    {
        string agentName = Llm!.Agents.GetValueOrDefault("Code")?.Threads.ContainsKey(threadKey) == true ? "Code" : "Dialogue";
        return Llm.Agents[agentName].GetOrCreateThread(threadKey);
    }

    // ── Thread endpoints ────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult GetThreads([FromQuery] bool includeInternal = false)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        Agent? dialogueAgent = Llm.Agents.GetValueOrDefault("Dialogue");
        Agent? codeAgent     = Llm.Agents.GetValueOrDefault("Code");

        // Collect all user-facing thread keys from both agents, deduped
        var allKeys = new Dictionary<string, (DateTime lastMessageAt, int count, string state, bool isCode)>();

        if (dialogueAgent is not null)
        {
            foreach (var kvp in dialogueAgent.Threads)
            {
                bool isCode = codeAgent?.Threads.ContainsKey(kvp.Key) == true;
                allKeys[kvp.Key] = (kvp.Value.LastMessageAt, kvp.Value.History.Count(m => m is UserMessage or AriResponse),
                    kvp.Value.State.ToString().ToLowerInvariant(), isCode);
            }
        }
        if (codeAgent is not null)
        {
            foreach (var kvp in codeAgent.Threads)
            {
                if (!allKeys.ContainsKey(kvp.Key))
                    allKeys[kvp.Key] = (kvp.Value.LastMessageAt, kvp.Value.History.Count(m => m is UserMessage or AriResponse),
                        kvp.Value.State.ToString().ToLowerInvariant(), true);
            }
        }

        List<ThreadEntry> threads = allKeys
            .Select(kvp =>
            {
                string? projectId   = ThreadProjects.TryGetValue(kvp.Key, out string? pid) ? pid : null;
                string? projectName = projectId is not null ? projectStore.Get(projectId)?.Name : null;
                return new ThreadEntry(kvp.Key, AgentName: null, IsInternal: false,
                    LastMessageAt: kvp.Value.lastMessageAt, MessageCount: kvp.Value.count,
                    State: kvp.Value.state, IsCodeMode: kvp.Value.isCode,
                    ProjectName: projectName, ProjectId: projectId);
            })
            .ToList();

        if (includeInternal)
        {
            HashSet<string> userKeys = new(dialogueAgent?.Threads.Keys ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            IEnumerable<ThreadEntry> internalThreads = Llm.Agents
                .Where(kvp => kvp.Key != "Dialogue" && kvp.Key != "Code")
                .SelectMany(kvp => kvp.Value.Threads
                    .Where(t => !userKeys.Contains(t.Key))
                    .Select(t => new ThreadEntry(t.Key, kvp.Key, IsInternal: true, t.Value.LastMessageAt, t.Value.History.Count)));
            threads.AddRange(internalThreads);
        }

        return Ok(threads.OrderByDescending(t => t.LastMessageAt).ToList());
    }

    [HttpPost]
    public IActionResult NewThread([FromBody] NewThreadRequest? req = null)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        string key = $"web-{Guid.NewGuid():N}";
        if (!string.IsNullOrWhiteSpace(req?.ProjectId) && projectStore.Get(req.ProjectId) is not null)
        {
            ThreadProjects[key] = req.ProjectId;
            PersistThreadProjects();
        }
        return Ok(new { key });
    }

    [HttpGet("{threadKey}/history")]
    public IActionResult GetHistory(string threadKey, [FromQuery] bool raw = false)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        // Hidden states (a still-streaming response, or one superseded by a newer prompt) are
        // never shown in the normal thread view; the raw/internal view keeps everything.
        List<ThreadItem> items = raw
            ? GetInternalThread(threadKey)?.History ?? new()
            : (GetThread(threadKey)?.History ?? new())
                .Where(i => i is not AriResponse { State: AriResponseState.Streaming or AriResponseState.Cancelled })
                .ToList();

        return Ok(items);
    }

    [HttpGet("{threadKey}/export")]
    public IActionResult ExportLog(string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        List<ThreadItem> items = GetThread(threadKey)?.History ?? new();
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
    /// SSE endpoint — one connection per client per thread.
    /// Sends an update event whenever the thread's history changes, plus a keep-alive ping every 20s.
    /// Payload: { "isProcessing": bool }
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
        bool isCodeMode    = Llm?.Agents.GetValueOrDefault("Code")?.Threads.ContainsKey(threadKey) == true;
        string payload     = JsonSerializer.Serialize(new { isProcessing, isRemembering, isCodeMode });
        await Response.WriteAsync($"data: {payload}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    [HttpPost("~/api/commands")]
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
        GetThread(threadKey)?.RemoveAttachment(name);
        return Ok();
    }

    [HttpGet("{threadKey}/attachments")]
    public IActionResult GetAttachments(string threadKey)
    {
        List<Attachment> staged  = pendingAttachments.TryGetValue(threadKey, out List<Attachment>? list) ? list : new();
        List<Attachment> onThread = GetThread(threadKey)?.GetAttachments().ToList() ?? new();
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

        List<ThreadItem> items = GetThread(threadKey)?.History ?? new();
        foreach (ThreadItem item in items)
        {
            if (item is not UserMessage msg || msg.Attachments is null) continue;
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
        ARI.LLM.Thread? thread = GetThread(threadKey);
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

        // Safeguard: reject prompts that are clearly too large for the context window.
        // Estimate at 4 chars/token; limit comes from the thread's configured MaxContextTokens (0 = unconfigured).
        Agent? dialogueAgent = Llm.Agents.GetValueOrDefault("Dialogue");
        (int _, int contextLimit) = dialogueAgent?.GetContextStats(dialogueAgent.GetThread(threadKey)) ?? (0, 0);
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
                bool isFirstMessage = GetThread(threadKey)?.History.Count is null or 0;
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

        try
        {
            string username = GetUsername();
            await Llm.PromptStreaming(threadKey, prompt, username, platformContext, async accumulated =>
            {
                string escaped = accumulated.Replace("\n", "\\n").Replace("\r", "");
                await Response.WriteAsync($"data: {escaped}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }, cancellationToken, messageAttachments: msgAtts, threadAttachments: threadAtts);
            await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await Response.WriteAsync("data: [CANCELLED]\n\n", cancellationToken);
        }
        catch (Exception ex)
        {
            await Response.WriteAsync($"data: [ERROR] {ex.Message}\n\n", cancellationToken);
        }

        await Response.Body.FlushAsync(cancellationToken);
    }
}

public record StreamRequest(string Prompt);
public record CommandRequest(string? ThreadKey, string Input);
public record NewThreadRequest(string? ProjectId);
public record InjectContextRequest(string Name, string Content);
public record ThreadEntry(string Key, string? AgentName, bool IsInternal, DateTime LastMessageAt, int MessageCount, string State = "active", bool IsCodeMode = false, string? ProjectName = null, string? ProjectId = null);

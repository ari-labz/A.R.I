using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ARI.LLM;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ARI.WebPanel.Controllers;

public class ChatController : Controller
{
    public IActionResult Index(string? threadKey) => View((object?)threadKey);
}

[Route("api/threads")]
[ApiController]
public class ThreadsController(LlmServiceHolder holder) : ControllerBase
{
    private LlmService? Llm => holder.Service;

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

    [HttpGet]
    public IActionResult GetThreads([FromQuery] bool includeInternal = false)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        List<ThreadEntry> threads = Llm.GetActiveThreadKeys()
            .Select(key => new ThreadEntry(
                key,
                AgentName:     null,
                IsInternal:    false,
                LastMessageAt: Llm.GetThreadLastMessageAt(key),
                MessageCount:  Llm.GetThreadItems(key).Count(m => m is UserMessage or AriResponse),
                State:         Llm.GetThreadState(key),
                IsCodeMode:    Llm.IsCodeThread(key)))
            .ToList();

        if (includeInternal)
        {
            IEnumerable<ThreadEntry> internalThreads = Llm.GetInternalThreads()
                .Select(t => new ThreadEntry(t.Key, t.AgentName, IsInternal: true, t.LastMessageAt, t.MessageCount));
            threads.AddRange(internalThreads);
        }

        return Ok(threads.OrderByDescending(t => t.LastMessageAt).ToList());
    }

    [HttpPost]
    public IActionResult NewThread()
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        string key = $"web-{Guid.NewGuid():N}";
        return Ok(new { key });
    }

    [HttpGet("{threadKey}/history")]
    public IActionResult GetHistory(string threadKey, [FromQuery] bool raw = false)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        IReadOnlyList<ThreadItem> items = raw
            ? Llm.GetInternalThreadItems(threadKey)
            : Llm.GetThreadItems(threadKey);

        return Ok(items);
    }

    [HttpGet("{threadKey}/export")]
    public IActionResult ExportLog(string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        var items = Llm.GetThreadItems(threadKey);
        var log = string.Join("\n\n", items.Select(i => i.ToString()));
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
        string payload     = JsonSerializer.Serialize(new { isProcessing, isRemembering });
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
    [RequestSizeLimit(50 * 1024 * 1024)] // 50 MB
    public async Task<IActionResult> AddAttachment(string threadKey, IFormFile file)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        if (file is null || file.Length == 0) return BadRequest("No file provided.");

        string mime = file.ContentType ?? "application/octet-stream";
        bool isImage = ImageMimes.Contains(mime);

        if (!isImage && BinaryMimes.Contains(mime))
            return StatusCode(415, new { error = $"{Path.GetExtension(file.FileName).TrimStart('.').ToUpper()} files cannot be attached as thread context — only images and text files are supported." });

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
        Llm.AddAttachment(threadKey, attachment);
        return Ok(new { name = file.FileName, isImage });
    }

    [HttpDelete("{threadKey}/attachments/{name}")]
    public IActionResult RemoveAttachment(string threadKey, string name)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        Llm.RemoveAttachment(threadKey, name);
        return Ok();
    }

    [HttpGet("{threadKey}/attachments")]
    public IActionResult GetAttachments(string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        return Ok(Llm.GetAttachments(threadKey).Select(a => new { a.Name, a.IsImage, a.MimeType }));
    }

    // ── Message Attachments (ephemeral — cleared after send) ────────────────────

    [HttpPost("{threadKey}/message-attachments")]
    [RequestSizeLimit(50 * 1024 * 1024)]
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
        Llm.AddMessageAttachment(threadKey, attachment);
        return Ok(new { name = file.FileName, isImage, mimeType = mime, content });
    }

    [HttpDelete("{threadKey}/message-attachments/{name}")]
    public IActionResult RemoveMessageAttachment(string threadKey, string name)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        Llm.RemoveMessageAttachment(threadKey, name);
        return Ok();
    }

    [HttpGet("{threadKey}/message-attachments")]
    public IActionResult GetMessageAttachments(string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        return Ok(Llm.GetMessageAttachments(threadKey).Select(a => new { a.Name, a.IsImage, a.MimeType, a.Content }));
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

        IReadOnlyList<ThreadItem> items = Llm.GetThreadItems(threadKey);
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
        var (_, contextLimit) = Llm.GetDialogueContextStats(threadKey);
        int effectiveLimit    = contextLimit > 0 ? contextLimit : 8000;
        int estimatedTokens   = prompt.Length / 4;
        if (estimatedTokens > effectiveLimit)
        {
            await Response.WriteAsync("data: Your message is too large for me to process. Please attach the content as a file instead.\n\n", cancellationToken);
            await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        try
        {
            string username = GetUsername();
            await Llm.PromptStreaming(threadKey, prompt, username, null, async accumulated =>
            {
                string escaped = accumulated.Replace("\n", "\\n").Replace("\r", "");
                await Response.WriteAsync($"data: {escaped}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }, cancellationToken);
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
public record ThreadEntry(string Key, string? AgentName, bool IsInternal, DateTime LastMessageAt, int MessageCount, string State = "active", bool IsCodeMode = false);

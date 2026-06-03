using System.Security.Claims;
using System.Text.Json;
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

        var threads = Llm.GetActiveThreadKeys()
            .Select(key => new ThreadEntry(
                key,
                AgentName:     null,
                IsInternal:    false,
                LastMessageAt: Llm.GetThreadLastMessageAt(key),
                MessageCount:  Llm.GetThreadItems(key).Count(m => m is UserMessage or AriResponse)))
            .ToList();

        if (includeInternal)
        {
            var internalThreads = Llm.GetInternalThreads()
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

        var channel = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions { SingleReader = true });
        using var watchHandle = Llm.WatchThread(threadKey, channel);

        // Send initial state so the client can sync immediately on connect.
        await SendWatchEvent(threadKey, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            // Wait up to 20s for an update; send a keep-alive ping either way.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            try
            {
                await channel.Reader.ReadAsync(timeoutCts.Token);
                // Drain any further queued notifications so we send one event per batch.
                while (channel.Reader.TryRead(out _)) { }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 20s timeout — send keep-alive comment so the connection stays alive through proxies.
                await Response.WriteAsync(": ping\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                continue;
            }

            await SendWatchEvent(threadKey, cancellationToken);
        }
    }

    private async Task SendWatchEvent(string threadKey, CancellationToken ct)
    {
        bool isProcessing = Llm?.IsThreadProcessing(threadKey) ?? false;
        string payload    = JsonSerializer.Serialize(new { isProcessing });
        await Response.WriteAsync($"data: {payload}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    [HttpPost("~/api/commands")]
    public async Task<IActionResult> RunCommand([FromBody] CommandRequest req)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        if (string.IsNullOrWhiteSpace(req.Input)) return BadRequest("Input is required.");

        string? result = await Llm.HandleCommandAsync(req.ThreadKey, req.Input);
        if (result is null) return BadRequest(new { error = $"Unknown command: {req.Input}" });
        return Ok(new { result });
    }

    // ── Attachments ─────────────────────────────────────────────────────────────

    private static readonly HashSet<string> ImageMimes = new(StringComparer.OrdinalIgnoreCase)
        { "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp" };

    [HttpPost("{threadKey}/attachments")]
    [RequestSizeLimit(50 * 1024 * 1024)] // 50 MB
    public async Task<IActionResult> AddAttachment(string threadKey, IFormFile file)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        if (file is null || file.Length == 0) return BadRequest("No file provided.");

        string mime = file.ContentType ?? "application/octet-stream";
        bool isImage = ImageMimes.Contains(mime);

        string content;
        if (isImage)
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            content = Convert.ToBase64String(ms.ToArray());
        }
        else
        {
            using var reader = new System.IO.StreamReader(file.OpenReadStream());
            content = await reader.ReadToEndAsync();
        }

        var attachment = new ThreadAttachment(file.FileName, content, isImage, mime);
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
        var list = Llm.GetAttachments(threadKey)
            .Select(a => new { a.Name, a.IsImage, a.MimeType });
        return Ok(list);
    }

    [HttpGet("{threadKey}/stream")]
    public async Task Stream(string threadKey, [FromQuery] string prompt, CancellationToken cancellationToken)
    {
        Response.Headers[HeaderNames.ContentType] = "text/event-stream";
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        if (Llm is null)
        {
            await Response.WriteAsync("data: [ERROR] ARI is not ready yet.\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        try
        {
            string username = GetUsername();
            string response = await Llm.Prompt(threadKey, prompt, username);
            string escaped  = response.Replace("\n", "\\n").Replace("\r", "");
            await Response.WriteAsync($"data: {escaped}\n\n", cancellationToken);
            await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        }
        catch (Exception ex)
        {
            await Response.WriteAsync($"data: [ERROR] {ex.Message}\n\n", cancellationToken);
        }

        await Response.Body.FlushAsync(cancellationToken);
    }
}

public record CommandRequest(string? ThreadKey, string Input);
public record ThreadEntry(string Key, string? AgentName, bool IsInternal, DateTime LastMessageAt, int MessageCount);

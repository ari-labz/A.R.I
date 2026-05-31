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

    [HttpGet]
    public IActionResult GetThreads()
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        var threads = Llm.GetActiveThreadKeys()
            .Select(key => new
            {
                key,
                lastMessageAt = Llm.GetThreadLastMessageAt(key),
                messageCount  = Llm.GetThreadHistory(key).Count(m => m.Role != "system")
            })
            .OrderByDescending(t => t.lastMessageAt)
            .ToList();

        return Ok(threads);
    }

    [HttpPost]
    public IActionResult NewThread()
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        string key = $"web-{Guid.NewGuid():N}";
        return Ok(new { key });
    }

    [HttpGet("{threadKey}/history")]
    public IActionResult GetHistory(string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        var messages = Llm.GetThreadDisplayHistory(threadKey)
            .Select(m => new
            {
                m.Role,
                m.Content,
                m.Timestamp,
                m.ThinkingSeconds,
                m.RecallNotes,
                m.ContextSummary
            });
        return Ok(messages);
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
            string response = await Llm.Prompt(threadKey, prompt);
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

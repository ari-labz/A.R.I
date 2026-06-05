using ARI.LLM;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

        var liveCalls = Llm?.GetAllLiveCalls().Select(c => new
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
            foreach (LlmService.LlmCallStat c in Llm.GetAllCallStats())
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
            foreach (string key in Llm.GetActiveThreadKeys())
            {
                var (used, limit) = Llm.GetDialogueContextStats(key);
                if (used <= 0) continue;
                contextStats.Add(new
                {
                    threadKey  = key,
                    agentName  = "Dialogue",
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
}

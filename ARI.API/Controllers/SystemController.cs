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

[Route("api/v1/admin")]
[ApiController]
public class SystemController(SystemInfo systemInfo) : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    /// <summary>
    /// SSE stream of the ARI.log tail — sends the last 100 lines on connect,
    /// then streams new lines as they are appended.
    /// </summary>
    [HttpGet("system/log")]
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
            if (newLength < position) position = 0;   // the log was rotated: start from the top of the new file
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

    [HttpGet("system/ram")]
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

    [HttpGet("system/hardware")]
    public IActionResult GetHardware()
    {
        // One report for everyone who wants to know about the machine: the wizard and the memory chart use the
        // usable/unified figures, the control panel's model page uses the raw RAM and GPU list.
        var report = ARI.API.Setup.HardwareInfo.Detect();
        long totalRam = systemInfo.GetTotalPhysicalRamBytes();
        List<SystemInfo.GpuInfo> gpus = systemInfo.GetGpus();
        return Ok(new
        {
            report.TotalBytes, report.UsableBytes, report.AvailableBytes, report.Unified, report.Gpu, report.Note,
            totalRamBytes = totalRam,
            gpus = gpus.Select(g => new { name = g.Name, vramBytes = g.VramBytes }).ToList()
        });
    }

    [HttpGet("system/stats")]
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

    [HttpGet("llamacpp")]
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

    /// <summary>Change how ARI manages llama.cpp. <c>path</c> points ARI at an install; <c>suppressUpdatePrompt</c> set
    /// to true stops the "update available" prompt. Send only what changes.</summary>
    [HttpPatch("llamacpp")]
    public IActionResult ChangeLlamaCpp([FromBody] LlamaCppChange req)
    {
        if (req.Path is not null && string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { error = "Path can't be empty." });
        if (req.Path is not null) Shared.LlamaCppSetPath?.Invoke(req.Path);
        if (req.SuppressUpdatePrompt == true) Shared.LlamaCppSuppressUpdates?.Invoke();
        return Ok(new { ok = true });
    }

    public sealed class LlamaCppChange { public string? Path { get; set; } public bool? SuppressUpdatePrompt { get; set; } }

}

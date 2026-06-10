using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ARI.LLM;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ARI.API.Controllers;

[Route("api/client")]
[ApiController]
public class ClientController : ControllerBase
{
    private readonly LlmServiceHolder llmHolder;
    private readonly ILogger<ClientController> logger;

    public ClientController(LlmServiceHolder llmHolder, ILogger<ClientController> logger)
    {
        this.llmHolder = llmHolder;
        this.logger    = logger;
    }

    // Pending tool calls: callId → TaskCompletionSource<string>
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> pendingFileCalls = new();

    [HttpGet]
    public async Task Connect()
    {
        logger.LogInformation("[Client] Incoming request — IsWebSocket: {IsWs}", HttpContext.WebSockets.IsWebSocketRequest);

        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsync("WebSocket connection required");
            return;
        }

        LlmService? llm = llmHolder.Service;
        if (llm is null)
        {
            HttpContext.Response.StatusCode = 503;
            return;
        }

        using WebSocket ws = await HttpContext.WebSockets.AcceptWebSocketAsync();
        logger.LogInformation("[Client] WebSocket accepted from {Remote}", HttpContext.Connection.RemoteIpAddress);

        string threadKey = $"client-{Guid.NewGuid():N}";
        List<string> fileTree = new();

        ARI.LLM.Thread codeThread;
        try
        {
            codeThread = llm.GetOrCreateCodeThread(threadKey);
            logger.LogInformation("[Client] Code thread ready: {Key}", threadKey);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Client] Failed to get code thread");
            await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, ex.Message, CancellationToken.None);
            return;
        }

        // Register read_file tool — sends request to this WS client and awaits the response
        codeThread.RegisterTool(
            "read_file",
            new
            {
                type = "function",
                function = new
                {
                    name        = "read_file",
                    description = "Read the contents of a file from the user's project on their machine.",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new { path = new { type = "string", description = "File path relative to project root" } },
                        required   = new[] { "path" },
                    },
                },
            },
            async (argsJson) =>
            {
                using JsonDocument doc = JsonDocument.Parse(argsJson);
                string path = doc.RootElement.GetProperty("path").GetString() ?? "";

                string callId = Guid.NewGuid().ToString("N");
                TaskCompletionSource<string> tcs = new();
                pendingFileCalls[callId] = tcs;

                await SendJson(ws, new { type = "read_file", callId, path });

                // Wait up to 30 s for the client to return the file
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
                cts.Token.Register(() => tcs.TrySetCanceled());
                try
                {
                    return await tcs.Task;
                }
                catch (OperationCanceledException)
                {
                    return $"[Error: client did not respond to read_file({path}) within 30s]";
                }
                finally
                {
                    pendingFileCalls.TryRemove(callId, out _);
                }
            });

        RegisterClientTool(codeThread, ws, "list_directory",
            "List the files and subdirectories at a path within the project.",
            new { type = "object", properties = new { path = new { type = "string", description = "Directory path relative to project root. Defaults to project root if omitted." } }, required = Array.Empty<string>() });

        RegisterClientTool(codeThread, ws, "search_files",
            "Search for a string across files in the project. Returns matching lines with file path and line number.",
            new { type = "object", properties = new { pattern = new { type = "string", description = "Text to search for (case-insensitive)" }, path = new { type = "string", description = "Directory to search in, relative to project root." }, glob = new { type = "string", description = "File filter e.g. '*.cs'. Defaults to all files." } }, required = new[] { "pattern" } });

        RegisterClientTool(codeThread, ws, "edit_file",
            "Make a targeted find-and-replace edit to an existing file. old_string must match exactly once. Use write_file for full rewrites.",
            new { type = "object", properties = new { path = new { type = "string", description = "File path relative to project root" }, old_string = new { type = "string", description = "Exact text to find (must appear exactly once)" }, new_string = new { type = "string", description = "Replacement text" } }, required = new[] { "path", "old_string", "new_string" } });

        RegisterClientTool(codeThread, ws, "write_file",
            "Write or create a file. Overwrites if it exists. Creates missing parent directories. Prefer edit_file for targeted changes.",
            new { type = "object", properties = new { path = new { type = "string", description = "File path relative to project root" }, content = new { type = "string", description = "Full content to write" } }, required = new[] { "path", "content" } });

        var state = new ConnectionState { ProjectRoot = "" };

        try
        {
            logger.LogInformation("[Client] Entering receive loop");
            await ReceiveLoop(ws, llm, codeThread, threadKey, fileTree, state);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Client] Receive loop error");
        }
        finally
        {
            foreach (string tool in new[] { "read_file", "list_directory", "search_files", "edit_file", "write_file" })
                codeThread.UnregisterTool(tool);
            logger.LogInformation("[Client] Session ended ({Thread})", threadKey);
        }
    }

    private void RegisterClientTool(ARI.LLM.Thread thread, WebSocket ws, string name, string description, object parameters)
    {
        thread.RegisterTool(
            name,
            new { type = "function", function = new { name, description, parameters } },
            async argsJson =>
            {
                string callId = Guid.NewGuid().ToString("N");
                TaskCompletionSource<string> tcs = new();
                pendingFileCalls[callId] = tcs;

                await SendJson(ws, new { type = name, callId, args = argsJson });

                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
                cts.Token.Register(() => tcs.TrySetCanceled());
                try   { return await tcs.Task; }
                catch (OperationCanceledException) { return $"[Error: client did not respond to {name} within 30s]"; }
                finally { pendingFileCalls.TryRemove(callId, out _); }
            });
    }

    private sealed class ConnectionState { public string ProjectRoot { get; set; } = ""; }

    private async Task ReceiveLoop(
        WebSocket ws,
        LlmService llm,
        ARI.LLM.Thread codeThread,
        string threadKey,
        List<string> fileTree,
        ConnectionState state)
    {
        byte[] buffer = new byte[1024 * 64];

        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            using MemoryStream ms = new();
            do
            {
                result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            string json = Encoding.UTF8.GetString(ms.ToArray());
            logger.LogDebug("[Client] Received: {Json}", json.Length > 120 ? json[..120] + "…" : json);
            using JsonDocument doc = JsonDocument.Parse(json);
            string type = doc.RootElement.GetProperty("type").GetString() ?? "";
            logger.LogInformation("[Client] Message type: {Type}", type);

            switch (type)
            {
                case "tree":
                    state.ProjectRoot = doc.RootElement.TryGetProperty("root", out JsonElement rootEl)
                        ? rootEl.GetString() ?? "" : "";
                    if (doc.RootElement.TryGetProperty("tree", out JsonElement treeEl))
                    {
                        fileTree.Clear();
                        foreach (JsonElement f in treeEl.EnumerateArray())
                        {
                            string? p = f.GetString();
                            if (p is not null) fileTree.Add(p);
                        }
                    }
                    logger.LogInformation("[Client] Tree received: {Count} files from {Root}", fileTree.Count, state.ProjectRoot);
                    await SendJson(ws, new { type = "tree_ack", count = fileTree.Count });
                    break;

                case "chat":
                    string prompt = doc.RootElement.TryGetProperty("prompt", out JsonElement promptEl)
                        ? promptEl.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(prompt)) break;

                    string platformContext = fileTree.Count > 0
                        ? $"The user has connected a project at `{state.ProjectRoot}`. File tree:\n```\n{string.Join("\n", fileTree)}\n```\n\n" +
                          "You have file system tools available — always use them rather than making assumptions about file contents:\n" +
                          "- `read_file` — read a file before answering any question about it\n" +
                          "- `list_directory` — explore a directory's contents\n" +
                          "- `search_files` — find a symbol, string, or pattern across the project\n" +
                          "- `edit_file` — make a targeted find-and-replace change to an existing file\n" +
                          "- `write_file` — create a new file or fully rewrite an existing one"
                        : null!;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await llm.PromptStreaming(
                                threadKey,
                                prompt,
                                username:        "user",
                                platformContext: platformContext,
                                onDelta:         async (delta) => await SendJson(ws, new { type = "delta", text = delta }),
                                ct:              CancellationToken.None);

                            await SendJson(ws, new { type = "done" });
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "[Client] Prompt error");
                            await SendJson(ws, new { type = "error", message = ex.Message });
                        }
                    });
                    break;

                case "file_content":
                    if (doc.RootElement.TryGetProperty("callId", out JsonElement cidEl) &&
                        doc.RootElement.TryGetProperty("content", out JsonElement contentEl))
                    {
                        string callId  = cidEl.GetString()    ?? "";
                        string content = contentEl.GetString() ?? "";
                        if (pendingFileCalls.TryGetValue(callId, out TaskCompletionSource<string>? tcs))
                            tcs.TrySetResult(content);
                    }
                    break;

                case "file_error":
                    if (doc.RootElement.TryGetProperty("callId", out JsonElement ecidEl) &&
                        doc.RootElement.TryGetProperty("error", out JsonElement errEl))
                    {
                        string callId = ecidEl.GetString() ?? "";
                        string error  = errEl.GetString()  ?? "";
                        if (pendingFileCalls.TryGetValue(callId, out TaskCompletionSource<string>? tcs))
                            tcs.TrySetResult($"[Error reading file: {error}]");
                    }
                    break;
            }
        }
    }

    private static async Task SendJson(WebSocket ws, object payload)
    {
        if (ws.State != WebSocketState.Open) return;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }
}

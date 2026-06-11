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

        // Allow the UI to bind tools to an existing thread (e.g. the active web-* thread)
        string threadKey = HttpContext.Request.Query.TryGetValue("threadKey", out Microsoft.Extensions.Primitives.StringValues tkv)
            && !string.IsNullOrWhiteSpace(tkv)
            ? tkv.ToString()
            : $"client-{Guid.NewGuid():N}";
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

                logger.LogInformation("[Client] → read_file  path={Path}  callId={CallId}", path, callId);
                await SendJson(ws, new { type = "read_file", callId, path });

                // Wait up to 30 s for the client to return the file
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
                cts.Token.Register(() => tcs.TrySetCanceled());
                try
                {
                    string result = await tcs.Task;
                    logger.LogInformation("[Client] ← read_file  callId={CallId}  bytes={Bytes}", callId, result.Length);
                    return result;
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("[Client] ← read_file TIMEOUT  callId={CallId}  path={Path}", callId, path);
                    return $"[Error: client did not respond to read_file({path}) within 30s]";
                }
                finally
                {
                    pendingFileCalls.TryRemove(callId, out _);
                }
            },
            argsJson =>
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(argsJson);
                    string label = System.IO.Path.GetFileName(doc.RootElement.GetProperty("path").GetString() ?? "file");
                    label = label.Replace("--", "&#45;&#45;");
                    return $"<!--ari-tool-start:read_file:{label}-->";
                }
                catch { return "<!--ari-tool-error:read_file:failed to parse tool args (malformed JSON)-->"; }
            },
            argsJson =>
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(argsJson);
                    string label = System.IO.Path.GetFileName(doc.RootElement.GetProperty("path").GetString() ?? "file");
                    label = label.Replace("--", "&#45;&#45;");
                    return $"<!--ari-tool-end:read_file:{label}-->";
                }
                catch { return "<!--ari-tool-error:read_file:failed to parse tool args (malformed JSON)-->"; }
            });

        RegisterClientTool(codeThread, ws, "list_directory",
            "List the files and subdirectories at a path within the project.",
            new { type = "object", properties = new { path = new { type = "string", description = "Directory path relative to project root. Defaults to project root if omitted." } }, required = Array.Empty<string>() },
            displayVerb: "Listing directory", displayDoneVerb: "Listed directory");

        RegisterClientTool(codeThread, ws, "search_files",
            "Search for a string across files in the project. Returns matching lines with file path and line number.",
            new { type = "object", properties = new { pattern = new { type = "string", description = "Text to search for (case-insensitive)" }, path = new { type = "string", description = "Directory to search in, relative to project root." }, glob = new { type = "string", description = "File filter e.g. '*.cs'. Defaults to all files." } }, required = new[] { "pattern" } },
            displayVerb: "Searching files", displayDoneVerb: "Searched files");

        RegisterClientTool(codeThread, ws, "edit_file",
            "Make a targeted find-and-replace edit to an existing file. old_string must match exactly once. Use write_file for full rewrites.",
            new { type = "object", properties = new { path = new { type = "string", description = "File path relative to project root" }, old_string = new { type = "string", description = "Exact text to find (must appear exactly once)" }, new_string = new { type = "string", description = "Replacement text" } }, required = new[] { "path", "old_string", "new_string" } },
            displayVerb: "Editing", displayDoneVerb: "Edited");

        RegisterClientTool(codeThread, ws, "write_file",
            "Write or create a file. Overwrites if it exists. Creates missing parent directories. Prefer edit_file for targeted changes.",
            new { type = "object", properties = new { path = new { type = "string", description = "File path relative to project root" }, content = new { type = "string", description = "Full content to write" } }, required = new[] { "path", "content" } },
            displayVerb: "Writing", displayDoneVerb: "Written");

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
    }

    private void RegisterClientTool(ARI.LLM.Thread thread, WebSocket ws, string name, string description, object parameters, string? displayVerb = null, string? displayDoneVerb = null)
    {
        Func<string, string>? MakeDisplay(string? verb, string markerType) => verb is null ? null : (argsJson =>
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(argsJson);
                string label = verb;
                if (doc.RootElement.TryGetProperty("path", out JsonElement pathEl))
                    label = System.IO.Path.GetFileName(pathEl.GetString() ?? verb);
                else if (doc.RootElement.TryGetProperty("pattern", out JsonElement patEl))
                    label = patEl.GetString() ?? verb;
                label = label.Replace("--", "&#45;&#45;");
                return $"<!--ari-tool-{markerType}:{name}:{label}-->";
            }
            catch { return $"<!--ari-tool-error:{name}:failed to parse tool args (malformed JSON)-->"; }
        });

        thread.RegisterTool(
            name,
            new { type = "function", function = new { name, description, parameters } },
            async argsJson =>
            {
                string callId = Guid.NewGuid().ToString("N");
                TaskCompletionSource<string> tcs = new();
                pendingFileCalls[callId] = tcs;

                logger.LogInformation("[Client] → {Tool}  callId={CallId}", name, callId);
                await SendJson(ws, new { type = name, callId, args = argsJson });

                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
                cts.Token.Register(() => tcs.TrySetCanceled());
                try
                {
                    string result = await tcs.Task;
                    logger.LogInformation("[Client] ← {Tool}  callId={CallId}  bytes={Bytes}", name, callId, result.Length);
                    return result;
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("[Client] ← {Tool} TIMEOUT  callId={CallId}", name, callId);
                    return $"[Error: client did not respond to {name} within 30s]";
                }
                finally { pendingFileCalls.TryRemove(callId, out _); }
            },
            MakeDisplay(displayVerb, "start"),
            MakeDisplay(displayDoneVerb ?? displayVerb, "end"));
    }

    private sealed class ConnectionState { public string ProjectRoot { get; set; } = ""; }

    private async Task ReceiveLoop(
        WebSocket ws,
        LlmService llm,
        ARI.LLM.Thread initialThread,
        string initialThreadKey,
        List<string> fileTree,
        ConnectionState state)
    {
        // These may be reassigned when the client sends a tree message with a threadKey to bind to
        ARI.LLM.Thread codeThread = initialThread;
        string         threadKey  = initialThreadKey;
        byte[] buffer = new byte[1024 * 64];

        try { await ReceiveLoopInner(); }
        finally
        {
            foreach (string tool in new[] { "read_file", "list_directory", "search_files", "edit_file", "write_file" })
                codeThread.UnregisterTool(tool);
            logger.LogInformation("[Client] Session ended ({Thread})", threadKey);
        }
        return;

        async Task ReceiveLoopInner()
        { while (ws.State == WebSocketState.Open)
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
                    // If the UI tells us which existing thread to bind tools to, re-register there
                    if (doc.RootElement.TryGetProperty("threadKey", out JsonElement bindKeyEl))
                    {
                        string bindKey = bindKeyEl.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(bindKey) && bindKey != threadKey)
                        {
                            logger.LogInformation("[Client] Binding tools to existing thread {BindKey}", bindKey);
                            ARI.LLM.Thread targetThread = llm.GetOrCreateCodeThread(bindKey);
                            // Move tool registrations from the client-* thread to the target web-* thread
                            foreach (string tool in new[] { "read_file", "list_directory", "search_files", "edit_file", "write_file" })
                                codeThread.UnregisterTool(tool);

                            RegisterClientTool(targetThread, ws, "read_file",
                                "Read the contents of a file from the user's project.",
                                new { type = "object", properties = new { path = new { type = "string", description = "File path relative to project root" } }, required = new[] { "path" } },
                                displayVerb: "Reading", displayDoneVerb: "Read");

                            RegisterClientTool(targetThread, ws, "list_directory",
                                "List files and subdirectories at a path within the project.",
                                new { type = "object", properties = new { path = new { type = "string", description = "Directory path relative to project root. Omit for root." } }, required = Array.Empty<string>() },
                                displayVerb: "Listing directory", displayDoneVerb: "Listed directory");

                            RegisterClientTool(targetThread, ws, "search_files",
                                "Search for a string across project files. Returns matching lines with file path and line number.",
                                new { type = "object", properties = new { pattern = new { type = "string", description = "Text to search for" }, path = new { type = "string", description = "Directory to search. Defaults to root." }, glob = new { type = "string", description = "File filter e.g. '*.cs'" } }, required = new[] { "pattern" } },
                                displayVerb: "Searching files", displayDoneVerb: "Searched files");

                            RegisterClientTool(targetThread, ws, "edit_file",
                                "Targeted find-and-replace on an existing file. old_string must match exactly once. Use write_file for full rewrites or new files.",
                                new { type = "object", properties = new { path = new { type = "string", description = "File path relative to project root" }, old_string = new { type = "string", description = "Exact text to find (must appear once)" }, new_string = new { type = "string", description = "Replacement text" } }, required = new[] { "path", "old_string", "new_string" } },
                                displayVerb: "Editing", displayDoneVerb: "Edited");

                            RegisterClientTool(targetThread, ws, "write_file",
                                "Write or create a file. Overwrites if it exists. Prefer edit_file for targeted changes.",
                                new { type = "object", properties = new { path = new { type = "string", description = "File path relative to project root" }, content = new { type = "string", description = "Full content to write" } }, required = new[] { "path", "content" } },
                                displayVerb: "Writing", displayDoneVerb: "Written");
                            // Update threadKey and codeThread for unregister on close
                            threadKey  = bindKey;
                            codeThread = targetThread;
                        }
                    }
                    logger.LogInformation("[Client] Tree received: {Count} files, bound to thread {Key}", fileTree.Count, threadKey);
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
                        logger.LogInformation("[Client] ← file_content  callId={CallId}  bytes={Bytes}  pending={Pending}", callId, content.Length, pendingFileCalls.ContainsKey(callId));
                        if (pendingFileCalls.TryGetValue(callId, out TaskCompletionSource<string>? tcs))
                            tcs.TrySetResult(content);
                        else
                            logger.LogWarning("[Client] ← file_content  callId={CallId}  NO PENDING CALL (too late?)", callId);
                    }
                    break;

                case "file_error":
                    if (doc.RootElement.TryGetProperty("callId", out JsonElement ecidEl) &&
                        doc.RootElement.TryGetProperty("error", out JsonElement errEl))
                    {
                        string callId = ecidEl.GetString() ?? "";
                        string error  = errEl.GetString()  ?? "";
                        logger.LogWarning("[Client] ← file_error  callId={CallId}  error={Error}", callId, error);
                        if (pendingFileCalls.TryGetValue(callId, out TaskCompletionSource<string>? tcs))
                            tcs.TrySetResult($"[Error reading file: {error}]");
                    }
                    break;
            }
        } }
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

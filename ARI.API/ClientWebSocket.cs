using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ARI.LLM;
using Microsoft.Extensions.Logging;

namespace ARI.API;

public static class ClientWebSocket
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> pendingFileCalls = new();

    public static async Task HandleAsync(WebSocket ws, LlmService llm, ILogger log)
    {
        string threadKey = $"client-{Guid.NewGuid():N}";
        var fileTree     = new List<string>();
        var state        = new ConnectionState();

        ARI.LLM.Thread codeThread;
        try
        {
            codeThread = llm.GetOrCreateCodeThread(threadKey);
            log.LogInformation("[Client] Code thread ready: {Key}", threadKey);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Client] Failed to get code thread — is Code agent configured?");
            await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, ex.Message, CancellationToken.None);
            return;
        }

        codeThread.RegisterTool(
            "read_file",
            new
            {
                type = "function",
                function = new
                {
                    name        = "read_file",
                    description = "Read a file from the user's project on their local machine.",
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
                using var doc = JsonDocument.Parse(argsJson);
                string path   = doc.RootElement.GetProperty("path").GetString() ?? "";
                string callId = Guid.NewGuid().ToString("N");

                var tcs = new TaskCompletionSource<string>();
                pendingFileCalls[callId] = tcs;

                await Send(ws, new { type = "read_file", call_id = callId, path });

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                cts.Token.Register(() => tcs.TrySetCanceled());
                try   { return await tcs.Task; }
                catch { return $"[Error: read_file({path}) timed out]"; }
                finally { pendingFileCalls.TryRemove(callId, out _); }
            });

        try
        {
            log.LogInformation("[Client] Entering receive loop");
            await ReceiveLoop(ws, llm, codeThread, threadKey, fileTree, state, log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Client] Receive loop crashed");
        }
        finally
        {
            codeThread.UnregisterTool("read_file");
            log.LogInformation("[Client] Session ended ({Thread})", threadKey);
        }
    }

    private sealed class ConnectionState { public string Root { get; set; } = ""; }

    private static async Task ReceiveLoop(
        WebSocket ws, LlmService llm, ARI.LLM.Thread codeThread,
        string threadKey, List<string> fileTree, ConnectionState state, ILogger log)
    {
        var buffer = new byte[64 * 1024];

        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            string json = Encoding.UTF8.GetString(ms.ToArray());
            log.LogInformation("[Client] Received message type from JSON (len={Len})", json.Length);

            JsonDocument doc;
            try   { doc = JsonDocument.Parse(json); }
            catch (Exception ex) { log.LogError(ex, "[Client] Failed to parse JSON"); continue; }

            using (doc)
            {
                string type = doc.RootElement.TryGetProperty("type", out var typeEl)
                    ? typeEl.GetString() ?? "" : "";
                log.LogInformation("[Client] Message type: {Type}", type);

                switch (type)
                {
                    case "tree":
                        state.Root = doc.RootElement.TryGetProperty("root", out var rootEl)
                            ? rootEl.GetString() ?? "" : "";
                        if (doc.RootElement.TryGetProperty("tree", out var treeEl))
                        {
                            fileTree.Clear();
                            foreach (var f in treeEl.EnumerateArray())
                            {
                                var p = f.GetString();
                                if (p is not null) fileTree.Add(p);
                            }
                        }
                        log.LogInformation("[Client] Tree received: {Count} files from {Root}", fileTree.Count, state.Root);
                        await Send(ws, new { type = "tree_ack", count = fileTree.Count });
                        break;

                    case "chat":
                        string prompt = doc.RootElement.TryGetProperty("prompt", out var promptEl)
                            ? promptEl.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(prompt)) break;

                        string? ctx = fileTree.Count > 0
                            ? $"The user has opened a project at `{state.Root}`. File tree:\n```\n{string.Join("\n", fileTree)}\n```\n\n" +
                              "Use the `read_file` tool to read files before answering code questions."
                            : null;

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await llm.PromptCodeStreaming(
                                    threadKey, prompt, "user", ctx,
                                    async delta =>
                                    {
                                        log.LogDebug("[Client] delta: {Len} chars", delta.Length);
                                        await Send(ws, new { type = "delta", text = delta });
                                    });
                                await Send(ws, new { type = "done" });
                            }
                            catch (Exception ex)
                            {
                                log.LogError(ex, "[Client] Prompt error");
                                await Send(ws, new { type = "error", message = ex.Message });
                            }
                        });
                        break;

                    case "file_content":
                        if ((doc.RootElement.TryGetProperty("call_id", out var cidEl) || doc.RootElement.TryGetProperty("callId", out cidEl)) &&
                            doc.RootElement.TryGetProperty("content", out var contentEl) &&
                            pendingFileCalls.TryGetValue(cidEl.GetString() ?? "", out var tcs1))
                            tcs1.TrySetResult(contentEl.GetString() ?? "");
                        break;

                    case "file_error":
                        if ((doc.RootElement.TryGetProperty("call_id", out var ecidEl) || doc.RootElement.TryGetProperty("callId", out ecidEl)) &&
                            doc.RootElement.TryGetProperty("error", out var errEl) &&
                            pendingFileCalls.TryGetValue(ecidEl.GetString() ?? "", out var tcs2))
                            tcs2.TrySetResult($"[Error: {errEl.GetString()}]");
                        break;

                    default:
                        log.LogWarning("[Client] Unknown message type: {Type}", type);
                        break;
                }
            }
        }
    }

    private static async Task Send(WebSocket ws, object payload)
    {
        if (ws.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }
}

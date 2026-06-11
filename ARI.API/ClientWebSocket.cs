using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ARI.LLM;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ARI.API;

public static class ClientWebSocket
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> pendingFileCalls = new();

    public static async Task HandleAsync(WebSocket ws, HttpContext ctx, LlmService llm, ILogger log)
    {
        // Use the threadKey from the query string if provided (binds tools to the active web-* thread)
        string threadKey = ctx.Request.Query.TryGetValue("threadKey", out var tkv) && !string.IsNullOrWhiteSpace(tkv)
            ? tkv.ToString()
            : $"client-{Guid.NewGuid():N}";

        log.LogInformation("[Client] Incoming WebSocket  threadKey={Key}", threadKey);

        var fileTree = new List<string>();
        var state    = new ConnectionState();

        ARI.LLM.Thread codeThread;
        try
        {
            codeThread = llm.GetOrCreateCodeThread(threadKey);
            log.LogInformation("[Client] Code thread ready: {Key}", threadKey);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Client] Failed to get code thread");
            await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, ex.Message, CancellationToken.None);
            return;
        }

        RegisterTools(codeThread, ws, log);

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
            foreach (string tool in new[] { "read_file", "list_directory", "search_files", "edit_file", "write_file" })
                codeThread.UnregisterTool(tool);
            log.LogInformation("[Client] Session ended ({Thread})", threadKey);
        }
    }

    private static void RegisterTools(ARI.LLM.Thread thread, WebSocket ws, ILogger log)
    {
        RegisterTool(thread, ws, log,
            name: "read_file",
            description: "Read the contents of a file from the user's project.",
            parameters: new { type = "object", properties = new { path = new { type = "string", description = "File path relative to project root" } }, required = new[] { "path" } },
            displayVerb: "Reading", displayDoneVerb: "Read",
            labelField: "path");

        RegisterTool(thread, ws, log,
            name: "list_directory",
            description: "List files and subdirectories at a path within the project.",
            parameters: new { type = "object", properties = new { path = new { type = "string", description = "Directory path relative to project root. Defaults to root." } }, required = Array.Empty<string>() },
            displayVerb: "Listing directory", displayDoneVerb: "Listed directory",
            labelField: "path");

        RegisterTool(thread, ws, log,
            name: "search_files",
            description: "Search for a string across files in the project. Returns matching lines with file path and line number.",
            parameters: new { type = "object", properties = new { pattern = new { type = "string", description = "Text to search for (case-insensitive)" }, path = new { type = "string", description = "Directory to search in, relative to project root." }, glob = new { type = "string", description = "File filter e.g. '*.cs'. Defaults to all files." } }, required = new[] { "pattern" } },
            displayVerb: "Searching", displayDoneVerb: "Searched",
            labelField: "pattern");

        RegisterTool(thread, ws, log,
            name: "edit_file",
            description: "Make a targeted find-and-replace edit to an existing file. old_string must match exactly once.",
            parameters: new { type = "object", properties = new { path = new { type = "string", description = "File path relative to project root" }, old_string = new { type = "string", description = "Exact text to find (must appear exactly once)" }, new_string = new { type = "string", description = "Replacement text" } }, required = new[] { "path", "old_string", "new_string" } },
            displayVerb: "Editing", displayDoneVerb: "Edited",
            labelField: "path",
            customDisplay: argsJson =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(argsJson);
                    string path   = doc.RootElement.TryGetProperty("path",       out var pe) ? pe.GetString() ?? "" : "";
                    string oldStr = doc.RootElement.TryGetProperty("old_string", out var oe) ? oe.GetString() ?? "" : "";
                    string newStr = doc.RootElement.TryGetProperty("new_string", out var ne) ? ne.GetString() ?? "" : "";
                    string label  = System.IO.Path.GetFileName(path).Replace("--", "&#45;&#45;");
                    int removed   = oldStr.Split('\n').Length;
                    int added     = newStr.Split('\n').Length;
                    return $"<!--ari-tool-start:edit_file:{label}|+{added}|-{removed}-->";
                }
                catch { return "<!--ari-tool-start:edit_file:file-->"; }
            },
            customDisplayDone: argsJson =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(argsJson);
                    string path    = doc.RootElement.TryGetProperty("path",       out var pe) ? pe.GetString() ?? "" : "";
                    string oldStr  = doc.RootElement.TryGetProperty("old_string", out var oe) ? oe.GetString() ?? "" : "";
                    string newStr  = doc.RootElement.TryGetProperty("new_string", out var ne) ? ne.GetString() ?? "" : "";
                    string label   = System.IO.Path.GetFileName(path).Replace("--", "&#45;&#45;");
                    int removed    = oldStr.Split('\n').Length;
                    int added      = newStr.Split('\n').Length;
                    string patch   = BuildPatch(oldStr, newStr);
                    string encoded = patch.Length <= 10_000
                        ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(patch))
                        : "";
                    return $"<!--ari-tool-end:edit_file:{label}|+{added}|-{removed}|{encoded}-->";
                }
                catch { return "<!--ari-tool-end:edit_file:file-->"; }
            });

        RegisterTool(thread, ws, log,
            name: "write_file",
            description: "Write or create a file. Overwrites if it exists. Prefer edit_file for targeted changes.",
            parameters: new { type = "object", properties = new { path = new { type = "string", description = "File path relative to project root" }, content = new { type = "string", description = "Full content to write" } }, required = new[] { "path", "content" } },
            displayVerb: "Writing", displayDoneVerb: "Written",
            labelField: "path",
            customDisplay: argsJson =>
            {
                try
                {
                    using var doc  = JsonDocument.Parse(argsJson);
                    string path    = doc.RootElement.TryGetProperty("path",    out var pe) ? pe.GetString() ?? "" : "";
                    string content = doc.RootElement.TryGetProperty("content", out var ce) ? ce.GetString() ?? "" : "";
                    string label   = System.IO.Path.GetFileName(path).Replace("--", "&#45;&#45;");
                    int added      = content.Split('\n').Length;
                    return $"<!--ari-tool-start:write_file:{label}|+{added}-->";
                }
                catch { return "<!--ari-tool-start:write_file:file-->"; }
            },
            customDisplayDone: argsJson =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(argsJson);
                    string path    = doc.RootElement.TryGetProperty("path",    out var pe) ? pe.GetString() ?? "" : "";
                    string content = doc.RootElement.TryGetProperty("content", out var ce) ? ce.GetString() ?? "" : "";
                    string label   = System.IO.Path.GetFileName(path).Replace("--", "&#45;&#45;");
                    int added      = content.Split('\n').Length;
                    string patch   = string.Join("\n", content.Split('\n').Select(l => "+" + l));
                    string encoded = patch.Length <= 10_000
                        ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(patch))
                        : "";
                    return $"<!--ari-tool-end:write_file:{label}|+{added}|{encoded}-->";
                }
                catch { return "<!--ari-tool-end:write_file:file-->"; }
            });
    }

    private static string BuildPatch(string oldStr, string newStr)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in oldStr.Split('\n'))
            sb.Append('-').AppendLine(line);
        foreach (var line in newStr.Split('\n'))
            sb.Append('+').AppendLine(line);
        return sb.ToString();
    }

    private static void RegisterTool(
        ARI.LLM.Thread thread, WebSocket ws, ILogger log,
        string name, string description, object parameters,
        string displayVerb, string displayDoneVerb, string labelField,
        Func<string, string>? customDisplay     = null,
        Func<string, string>? customDisplayDone = null)
    {
        Func<string, string> MakeDisplay(string verb, string markerType) => argsJson =>
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                string label = doc.RootElement.TryGetProperty(labelField, out var el) ? el.GetString() ?? "" : "";
                label = System.IO.Path.GetFileName(label).Replace("--", "&#45;&#45;");
                if (string.IsNullOrWhiteSpace(label)) label = "file";
                return $"<!--ari-tool-{markerType}:{name}:{label}-->";
            }
            catch { return $"<!--ari-tool-error:{name}:failed to parse tool args (malformed JSON)-->"; }
        };

        var displayFn     = customDisplay     ?? MakeDisplay(displayVerb,     "start");
        var displayDoneFn = customDisplayDone ?? MakeDisplay(displayDoneVerb, "end");

        thread.RegisterTool(
            name,
            new { type = "function", function = new { name, description, parameters } },
            async argsJson =>
            {
                string callId = Guid.NewGuid().ToString("N");
                var tcs = new TaskCompletionSource<string>();
                pendingFileCalls[callId] = tcs;

                log.LogInformation("[Client] → {Tool}  callId={CallId}", name, callId);
                await Send(ws, new { type = name, callId, args = argsJson });

                // Write/edit operations on large files can take longer to round-trip through the bridge
                int timeoutSeconds = name is "write_file" or "edit_file" ? 90 : 30;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                cts.Token.Register(() => tcs.TrySetCanceled());
                try
                {
                    string result = await tcs.Task;
                    log.LogInformation("[Client] ← {Tool}  callId={CallId}  bytes={Bytes}", name, callId, result.Length);
                    return result;
                }
                catch (OperationCanceledException)
                {
                    log.LogWarning("[Client] ← {Tool} TIMEOUT  callId={CallId}", name, callId);
                    return $"[Error: client did not respond to {name} within 30s]";
                }
                finally { pendingFileCalls.TryRemove(callId, out _); }
            },
            displayFn,
            displayDoneFn);
    }

    private sealed class ConnectionState { public string Root { get; set; } = ""; }

    private static async Task ReceiveLoop(
        WebSocket ws, LlmService llm, ARI.LLM.Thread initialThread,
        string initialThreadKey, List<string> fileTree, ConnectionState state, ILogger log)
    {
        ARI.LLM.Thread codeThread = initialThread;
        string         threadKey  = initialThreadKey;
        var buffer = new byte[64 * 1024];

        try { await Inner(); }
        finally
        {
            foreach (string tool in new[] { "read_file", "list_directory", "search_files", "edit_file", "write_file" })
                codeThread.UnregisterTool(tool);
        }
        return;

        async Task Inner()
        {
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
                JsonDocument doc;
                try   { doc = JsonDocument.Parse(json); }
                catch (Exception ex) { log.LogError(ex, "[Client] Failed to parse JSON"); continue; }

                using (doc)
                {
                    string type = doc.RootElement.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";
                    log.LogInformation("[Client] Message type: {Type}", type);

                    switch (type)
                    {
                        case "tree":
                            state.Root = doc.RootElement.TryGetProperty("root", out var rootEl) ? rootEl.GetString() ?? "" : "";
                            if (doc.RootElement.TryGetProperty("tree", out var treeEl))
                            {
                                fileTree.Clear();
                                foreach (var f in treeEl.EnumerateArray())
                                {
                                    var p = f.GetString();
                                    if (p is not null) fileTree.Add(p);
                                }
                            }

                            // If the tree body specifies a different threadKey, rebind tools to that thread
                            if (doc.RootElement.TryGetProperty("threadKey", out var bindKeyEl))
                            {
                                string bindKey = bindKeyEl.GetString() ?? "";
                                if (!string.IsNullOrWhiteSpace(bindKey) && bindKey != threadKey)
                                {
                                    log.LogInformation("[Client] Rebinding tools from {Old} → {New}", threadKey, bindKey);
                                    foreach (string tool in new[] { "read_file", "list_directory", "search_files", "edit_file", "write_file" })
                                        codeThread.UnregisterTool(tool);
                                    codeThread = llm.GetOrCreateCodeThread(bindKey);
                                    RegisterTools(codeThread, ws, log);
                                    threadKey = bindKey;
                                }
                            }

                            log.LogInformation("[Client] Tree received: {Count} files, bound to thread {Key}", fileTree.Count, threadKey);
                            await Send(ws, new { type = "tree_ack", count = fileTree.Count });
                            break;

                        case "file_content":
                            if ((doc.RootElement.TryGetProperty("callId", out var cidEl) || doc.RootElement.TryGetProperty("call_id", out cidEl))
                                && doc.RootElement.TryGetProperty("content", out var contentEl))
                            {
                                string callId  = cidEl.GetString()     ?? "";
                                string content = contentEl.GetString() ?? "";
                                log.LogInformation("[Client] ← file_content  callId={CallId}  bytes={Bytes}  pending={Pending}", callId, content.Length, pendingFileCalls.ContainsKey(callId));
                                if (pendingFileCalls.TryGetValue(callId, out var tcs))
                                    tcs.TrySetResult(content);
                                else
                                    log.LogWarning("[Client] ← file_content  callId={CallId}  NO PENDING CALL", callId);
                            }
                            break;

                        case "file_error":
                            if ((doc.RootElement.TryGetProperty("callId", out var ecidEl) || doc.RootElement.TryGetProperty("call_id", out ecidEl))
                                && doc.RootElement.TryGetProperty("error", out var errEl))
                            {
                                string callId = ecidEl.GetString() ?? "";
                                string error  = errEl.GetString()  ?? "";
                                log.LogWarning("[Client] ← file_error  callId={CallId}  error={Error}", callId, error);
                                if (pendingFileCalls.TryGetValue(callId, out var tcs))
                                    tcs.TrySetResult($"[Error: {error}]");
                            }
                            break;

                        default:
                            log.LogWarning("[Client] Unknown message type: {Type}", type);
                            break;
                    }
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

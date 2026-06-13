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
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> pendingFileCalls  = new();
    private static readonly ConcurrentDictionary<string, string>                       pendingCallLabels = new();

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
            foreach (string tool in ClientToolNames)
                codeThread.UnregisterTool(tool);
            log.LogInformation("[Client] Session ended ({Thread})", threadKey);
        }
    }

    private static readonly string[] ClientToolNames =
        { "read_file", "list_directory", "search_files", "edit_file", "write_file", "run_command" };

    private static void RegisterTools(ARI.LLM.Thread thread, WebSocket ws, ILogger log)
    {
        RegisterTool(thread, ws, log,
            name: "run_command",
            description: "Run a shell command in the project's root directory and get back its stdout, stderr and exit code. Use this to build, test, or inspect the project after making changes — e.g. 'dotnet build', 'dotnet test', 'git status'. Commands not on the user's allow list require their approval before running, so prefer concrete, non-destructive commands and do not chain commands with ; && or |.",
            parameters: new { type = "object", properties = new { command = new { type = "string", description = "The exact command line to run, e.g. 'dotnet build'." } }, required = new[] { "command" } },
            displayVerb: "Running", displayDoneVerb: "Ran",
            labelField: "command",
            customDisplay:     argsJson => RunCommandMarker(argsJson, "start"),
            customDisplayDone: argsJson => RunCommandMarker(argsJson, "end"));

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
                    string label  = System.IO.Path.GetFileName(path.Trim('"', '\'', ' ', '\\')).Replace("--", "&#45;&#45;");
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
                    string label   = System.IO.Path.GetFileName(path.Trim('"', '\'', ' ', '\\')).Replace("--", "&#45;&#45;");
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
                    string label   = System.IO.Path.GetFileName(path.Trim('"', '\'', ' ', '\\')).Replace("--", "&#45;&#45;");
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
                    string label   = System.IO.Path.GetFileName(path.Trim('"', '\'', ' ', '\\')).Replace("--", "&#45;&#45;");
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

    /// <summary>Builds a tool card marker for run_command, sanitising the command for the marker grammar.</summary>
    private static string RunCommandMarker(string argsJson, string markerType)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            string cmd = doc.RootElement.TryGetProperty("command", out var ce) ? ce.GetString() ?? "" : "";
            cmd = cmd.Replace("\r", " ").Replace("\n", " ").Trim();
            if (cmd.Length > 60) cmd = cmd[..60] + "…";
            // Marker grammar uses ':' '>' '|' '--' as delimiters — neutralise them in the label.
            string safe = cmd.Replace("--", "&#45;&#45;").Replace(":", "∶").Replace(">", "&gt;").Replace("|", "¦");
            if (string.IsNullOrWhiteSpace(safe)) safe = "command";
            return $"<!--ari-tool-{markerType}:run_command:{safe}-->";
        }
        catch { return $"<!--ari-tool-{markerType}:run_command:command-->"; }
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
                label = System.IO.Path.GetFileName(label.Trim('"', '\'', ' ', '\\')).Replace("--", "&#45;&#45;");
                if (string.IsNullOrWhiteSpace(label)) label = "file";
                return $"<!--ari-tool-{markerType}:{name}:{label}-->";
            }
            catch { return $"<!--ari-tool-error:{name}:failed to parse tool args (malformed JSON)-->"; }
        };

        var displayFn     = customDisplay     ?? MakeDisplay(displayVerb,     "start");
        var displayDoneFn = customDisplayDone ?? MakeDisplay(displayDoneVerb, "end");

        // Streaming display: emits a live start marker during arg streaming so the UI can
        // show and animate line counts as old_string / new_string / content arrive token-by-token.
        Func<string, string?>? streamingDisplayFn = (name is "edit_file" or "write_file")
            ? partialJson =>
            {
                string path = PartialJsonExtractString(partialJson, "path");
                if (string.IsNullOrEmpty(path)) return null; // wait until path is known
                string label = System.IO.Path.GetFileName(path.Trim('"', '\'', ' ', '\\')).Replace("--", "&#45;&#45;");
                if (name == "edit_file")
                {
                    int added   = PartialJsonCountNewlines(partialJson, "new_string");
                    int removed = PartialJsonCountNewlines(partialJson, "old_string");
                    return $"<!--ari-tool-start:edit_file:{label}|+{added}|-{removed}-->";
                }
                else // write_file
                {
                    int added = PartialJsonCountNewlines(partialJson, "content");
                    return $"<!--ari-tool-start:write_file:{label}|+{added}-->";
                }
            }
            : null;

        thread.RegisterTool(
            name,
            new { type = "function", function = new { name, description, parameters } },
            async argsJson =>
            {
                argsJson = NormalizePathArg(argsJson);
                string callId = Guid.NewGuid().ToString("N");
                string label  = ExtractLogLabel(argsJson, labelField);
                var tcs = new TaskCompletionSource<string>();
                pendingFileCalls[callId]  = tcs;
                pendingCallLabels[callId] = label;

                log.LogInformation("[Client] → {Tool}  {Label}  callId={CallId}", name, label, callId);
                await Send(ws, new { type = name, callId, args = argsJson });

                // run_command can wait on a build/test plus user confirmation; write/edit round-trips
                // on large files are slower than reads.
                int timeoutSeconds = name switch
                {
                    "run_command"            => 900,
                    "write_file" or "edit_file" => 90,
                    _                         => 30
                };
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                cts.Token.Register(() => tcs.TrySetCanceled());
                try
                {
                    string result = await tcs.Task;
                    log.LogInformation("[Client] ← {Tool}  {Label}  callId={CallId}  bytes={Bytes}", name, label, callId, result.Length);
                    return result;
                }
                catch (OperationCanceledException)
                {
                    log.LogWarning("[Client] ← {Tool} TIMEOUT  {Label}  callId={CallId}", name, label, callId);
                    return $"[Error: client did not respond to {name} within {timeoutSeconds}s]";
                }
                finally
                {
                    pendingFileCalls.TryRemove(callId, out _);
                    pendingCallLabels.TryRemove(callId, out _);
                }
            },
            displayFn,
            displayDoneFn,
            streamingDisplayFn);
    }

    /// <summary>
    /// Scans partial (streaming) JSON text for the value of a string field.
    /// Stops at the closing quote of the value, handling escape sequences.
    /// Returns empty string if the field has not started arriving yet.
    /// </summary>
    private static string PartialJsonExtractString(string partial, string fieldName)
    {
        int pos = FindJsonFieldValue(partial, fieldName);
        if (pos < 0) return "";
        var sb = new System.Text.StringBuilder();
        bool esc = false;
        for (int i = pos; i < partial.Length; i++)
        {
            char c = partial[i];
            if (esc) { sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c == 'r' ? '\r' : c); esc = false; }
            else if (c == '\\') esc = true;
            else if (c == '"') break;
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Counts newlines (\n escape sequences) in a streaming JSON string field.
    /// Counts actual characters as they arrive — one \n escape = one newline.
    /// Returns 0 if the field has not started arriving yet.
    /// </summary>
    private static int PartialJsonCountNewlines(string partial, string fieldName)
    {
        int pos = FindJsonFieldValue(partial, fieldName);
        if (pos < 0) return 0;
        int count = 0;
        bool esc = false;
        for (int i = pos; i < partial.Length; i++)
        {
            char c = partial[i];
            if (esc) { if (c == 'n') count++; esc = false; }
            else if (c == '\\') esc = true;
            else if (c == '"') break;
        }
        return count;
    }

    /// <summary>Returns the index of the first character inside the string value of a JSON field, or -1.</summary>
    private static int FindJsonFieldValue(string partial, string fieldName)
    {
        // Try both "field": " and "field" : " (with space around colon)
        foreach (string pattern in new[] { $"\"{fieldName}\":\"", $"\"{fieldName}\": \"" })
        {
            int idx = partial.IndexOf(pattern, StringComparison.Ordinal);
            if (idx >= 0) return idx + pattern.Length;
        }
        return -1;
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
            foreach (string tool in ClientToolNames)
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
                                    foreach (string tool in ClientToolNames)
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
                                string flabel  = pendingCallLabels.TryGetValue(callId, out var fl) ? fl : "";
                                log.LogInformation("[Client] ← file_content  {Label}  callId={CallId}  bytes={Bytes}  pending={Pending}", flabel, callId, content.Length, pendingFileCalls.ContainsKey(callId));
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
                                string elabel = pendingCallLabels.TryGetValue(callId, out var el2) ? el2 : "";
                                log.LogWarning("[Client] ← file_error  {Label}  callId={CallId}  error={Error}", elabel, callId, error);
                                if (pendingFileCalls.TryGetValue(callId, out var tcs))
                                    tcs.TrySetResult($"[Error: {SanitizeClientError(error)}]");
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

    /// <summary>
    /// Rewrites the "path" field in a tool-call args JSON so that any surrounding quotes the model
    /// accidentally included in the value are stripped before the path reaches the filesystem.
    /// e.g. {"path": "\"Foo/Bar.cs\""} → {"path": "Foo/Bar.cs"}
    /// </summary>
    private static string NormalizePathArg(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (!doc.RootElement.TryGetProperty("path", out JsonElement pathEl)) return argsJson;
            string raw  = pathEl.GetString() ?? "";
            string clean = raw.Trim('"', '\'', ' ', '\\');
            if (clean == raw) return argsJson;

            // Rebuild JSON with the cleaned path — re-serialize the whole object.
            var rebuilt = new Dictionary<string, JsonElement>();
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                rebuilt[prop.Name] = prop.Value;

            // Serialize with the clean path value substituted.
            using var ms = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            writer.WriteStartObject();
            foreach (var kv in rebuilt)
            {
                if (kv.Key == "path")
                    writer.WriteString("path", clean);
                else
                {
                    writer.WritePropertyName(kv.Key);
                    kv.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch { return argsJson; }
    }

    /// <summary>
    /// Strips raw OS-level detail from client-side file errors before they are returned to the model.
    /// ENOENT errors include the (potentially corrupted) file path, which the model can copy verbatim
    /// into the next call, causing an escaping spiral. Replace them with a clean message.
    /// </summary>
    private static string SanitizeClientError(string error)
    {
        // ENOENT: no such file or directory, open '/path/to/file' → File not found.
        if (error.Contains("ENOENT", StringComparison.OrdinalIgnoreCase))
            return "File not found. Check the path is correct and relative to the project root.";

        // EACCES / EPERM: permission denied → clean message
        if (error.Contains("EACCES", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("EPERM",  StringComparison.OrdinalIgnoreCase))
            return "Permission denied accessing that file.";

        return error;
    }

    private static string ExtractLogLabel(string argsJson, string labelField)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            string value = doc.RootElement.TryGetProperty(labelField, out var el) ? el.GetString() ?? "" : "";
            return string.IsNullOrWhiteSpace(value) ? "" : System.IO.Path.GetFileName(value);
        }
        catch { return ""; }
    }

    private static async Task Send(WebSocket ws, object payload)
    {
        if (ws.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }
}

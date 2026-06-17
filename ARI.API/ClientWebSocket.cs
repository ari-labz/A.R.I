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

    // Persistent per-thread file tool state — survives WebSocket reconnections until the thread is deleted.
    private static readonly ConcurrentDictionary<string, FileToolState> threadFileState = new();

    private sealed class FileToolState
    {
        // Files modified by write_file or edit_file this session. Must be re-read before further edits.
        public ConcurrentDictionary<string, byte> DirtyFiles   { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Consecutive edit_file old_string failures per file. Reset on read_file.
        public ConcurrentDictionary<string, int>  EditFailures { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task HandleAsync(WebSocket ws, HttpContext ctx, LLMModule llm, ILogger log)
    {
        // Use the threadKey from the query string if provided (binds tools to the active web-* thread)
        string threadKey = ctx.Request.Query.TryGetValue("threadKey", out var tkv) && !string.IsNullOrWhiteSpace(tkv)
            ? tkv.ToString()
            : $"client-{Guid.NewGuid():N}";

        log.LogInformation("[Client] Incoming WebSocket  threadKey={Key}", threadKey);

        var fileTree = new List<string>();
        var state    = new ConnectionState();

        // Get or create persistent file-tool state for this thread.
        FileToolState fileState = threadFileState.GetOrAdd(threadKey, _ => new FileToolState());

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

        RegisterTools(codeThread, ws, log, fileState);

        try
        {
            log.LogInformation("[Client] Entering receive loop");
            await ReceiveLoop(ws, llm, codeThread, threadKey, fileTree, state, fileState, log);
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
        { "preview_file", "read_file", "list_directory", "search_files", "find_files", "edit_file", "write_file",
          "delete_file", "move_file", "run_command", "update_todos" };

    private static void RegisterTools(ARI.LLM.Thread thread, WebSocket ws, ILogger log, FileToolState fileState)
    {
        RegisterTool(thread, ws, log,
            name: "run_command",
            description: "Run a shell command in the project's root directory and get back its stdout, stderr and exit code. Use this to build, test, or inspect the project after making changes — e.g. 'dotnet build', 'dotnet test', 'git status'. Commands not on the user's allow list require their approval before running, so prefer concrete, non-destructive commands and do not chain commands with ; && or |.",
            parameters: new { type = "object", properties = new { command = new { type = "string", description = "The exact command line to run, e.g. 'dotnet build'." } }, required = new[] { "command" } },
            displayVerb: "Running", displayDoneVerb: "Ran",
            labelField: "command",
            customDisplay:     argsJson => RunCommandMarker(argsJson, "start"),
            customDisplayDone: argsJson => RunCommandMarker(argsJson, "end"),
            preCheck: argsJson => CheckRunCommand(argsJson));

        RegisterTool(thread, ws, log,
            name: "preview_file",
            description: "Get a structural outline of a file — line count, size, and landmarks (classes, methods, JSON keys, headings) with line numbers. Call this before read_file on any unfamiliar file to find the right line range.",
            parameters: new { type = "object", properties = new { path = new { type = "string", description = "File path relative to project root." } }, required = new[] { "path" } },
            displayVerb: "Previewing", displayDoneVerb: "Previewed",
            labelField: "path");

        RegisterTool(thread, ws, log,
            name: "read_file",
            description: "Read the contents of a file from the user's project.",
            parameters: new { type = "object", properties = new { path = new { type = "string", description = "File path relative to project root" } }, required = new[] { "path" } },
            displayVerb: "Reading", displayDoneVerb: "Read",
            labelField: "path",
            postHook: (argsJson, result) => { ClearDirtyAndFailures(argsJson, fileState); return null; });

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
            },
            preCheck: argsJson => CheckDirty(argsJson, fileState),
            postHook: (argsJson, result) => TrackEditResult(argsJson, result, fileState));

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
            },
            preCheck: argsJson => CheckDirty(argsJson, fileState),
            postHook: (argsJson, result) => { MarkWriteDirty(argsJson, result, fileState); return null; });

        RegisterTool(thread, ws, log,
            name: "find_files",
            description: "Find files by name with a glob pattern, e.g. '*.cs', 'Token*.cs', or '**/Security/*.cs'. Returns paths relative to the project root. Use search_files to match file contents.",
            parameters: new { type = "object", properties = new { pattern = new { type = "string", description = "Glob pattern, e.g. '*.cs' or '**/Token*.cs'." }, path = new { type = "string", description = "Directory to search under, relative to project root. Defaults to root." } }, required = new[] { "pattern" } },
            displayVerb: "Finding", displayDoneVerb: "Found",
            labelField: "pattern");

        RegisterTool(thread, ws, log,
            name: "delete_file",
            description: "Delete a file from the project. Use only when explicitly required (e.g. removing a file after merging its contents elsewhere). The user is asked to confirm before the deletion happens.",
            parameters: new { type = "object", properties = new { path = new { type = "string", description = "File path relative to project root." } }, required = new[] { "path" } },
            displayVerb: "Deleting", displayDoneVerb: "Deleted",
            labelField: "path");

        RegisterTool(thread, ws, log,
            name: "move_file",
            description: "Move or rename a file within the project. Creates destination directories as needed. Fails if the destination already exists.",
            parameters: new { type = "object", properties = new { source = new { type = "string", description = "Current file path relative to project root." }, destination = new { type = "string", description = "New file path relative to project root." } }, required = new[] { "source", "destination" } },
            displayVerb: "Moving", displayDoneVerb: "Moved",
            labelField: "source");

        // The checklist lives on the thread and must execute IN-PROCESS — never round-trip to the client.
        thread.RegisterTodosTool();
    }

    // ── File-tool guardrail helpers ──────────────────────────────────────────────

    /// <summary>Blocks run_command from being used as a file reader (cat/head/tail/etc. or bare filename).</summary>
    private static string? CheckRunCommand(string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            string cmd = (doc.RootElement.TryGetProperty("command", out var ce) ? ce.GetString() ?? "" : "").Trim();
            if (string.IsNullOrWhiteSpace(cmd)) return null;

            string firstToken = cmd.Split(' ', 2)[0].ToLowerInvariant();
            if (firstToken is "cat" or "head" or "tail" or "sed" or "awk" or "type")
                return $"Use read_file instead of '{firstToken}' to read files. Pass the file path directly to read_file.";

            // Bare filename with a source-file extension — model trying to view a file via the shell
            if (!cmd.Contains(' ') && LooksLikeFilePath(cmd))
                return $"'{cmd}' looks like a file path. Use read_file to read file contents — do not pass file paths to run_command.";

            return null;
        }
        catch { return null; }
    }

    private static bool LooksLikeFilePath(string cmd)
    {
        string ext = System.IO.Path.GetExtension(cmd);
        if (string.IsNullOrEmpty(ext)) return false;
        return ext.ToLowerInvariant() is ".cs" or ".xaml" or ".json" or ".xml" or ".md" or ".txt"
            or ".js" or ".ts" or ".tsx" or ".css" or ".html" or ".yml" or ".yaml"
            or ".csproj" or ".sln" or ".config" or ".toml" or ".py" or ".sh" or ".ps1";
    }

    /// <summary>Blocks edit_file or write_file if the file is dirty (modified since last read).</summary>
    private static string? CheckDirty(string argsJson, FileToolState fileState)
    {
        string path = ExtractToolPath(argsJson);
        if (!string.IsNullOrEmpty(path) && fileState.DirtyFiles.ContainsKey(path))
            return $"[Blocked] '{path}' has been modified this session. You must call read_file on it before making further changes.";
        return null;
    }

    /// <summary>On successful read_file, clears the dirty flag and failure count for that file.</summary>
    private static void ClearDirtyAndFailures(string argsJson, FileToolState fileState)
    {
        string path = ExtractToolPath(argsJson);
        if (!string.IsNullOrEmpty(path))
        {
            fileState.DirtyFiles.TryRemove(path, out _);
            fileState.EditFailures.TryRemove(path, out _);
        }
    }

    /// <summary>
    /// Tracks edit_file outcomes. On success: marks dirty, resets failure count.
    /// On old_string failure: increments counter; after 2 failures appends a hard block message.
    /// </summary>
    private static string? TrackEditResult(string argsJson, string result, FileToolState fileState)
    {
        string path = ExtractToolPath(argsJson);
        if (string.IsNullOrEmpty(path)) return null;

        bool isOldStringFailure = result.Contains("old_string not found", StringComparison.OrdinalIgnoreCase)
                               || result.Contains("No changes made", StringComparison.OrdinalIgnoreCase);

        if (isOldStringFailure)
        {
            int count = fileState.EditFailures.AddOrUpdate(path, 1, (_, c) => c + 1);
            if (count >= 2)
                return result + $"\n\n[BLOCKED] edit_file has failed {count} times on '{path}' due to old_string mismatches. You MUST call read_file on this file before attempting any further edits to it.";
        }
        else if (!result.StartsWith("[Error:", StringComparison.OrdinalIgnoreCase))
        {
            // Successful edit — mark dirty, reset failure counter
            fileState.EditFailures.TryRemove(path, out _);
            fileState.DirtyFiles.TryAdd(path, 0);
        }

        return null;
    }

    /// <summary>On successful write_file, marks the file as dirty.</summary>
    private static void MarkWriteDirty(string argsJson, string result, FileToolState fileState)
    {
        string path = ExtractToolPath(argsJson);
        if (!string.IsNullOrEmpty(path) && result.StartsWith("Successfully", StringComparison.OrdinalIgnoreCase))
            fileState.DirtyFiles.TryAdd(path, 0);
    }

    private static string ExtractToolPath(string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            return (doc.RootElement.TryGetProperty("path", out var pe) ? pe.GetString() ?? "" : "").Trim();
        }
        catch { return ""; }
    }

    // ── End guardrail helpers ────────────────────────────────────────────────────

    /// <summary>Compact, capped project map injected as persistent context so the model orients fast.</summary>
    private static string BuildProjectMap(List<string> files)
    {
        if (files.Count == 0) return "";
        const int CAP = 200;
        var sorted = files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(CAP);
        string body = string.Join("\n", sorted);
        if (files.Count > CAP)
            body += $"\n... ({files.Count - CAP} more — use find_files / list_directory to explore)";
        return $"The project contains {files.Count} source files:\n{body}";
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
        Func<string, string>? customDisplayDone = null,
        Func<string, string?>? preCheck         = null,
        Func<string, string, string?>? postHook = null)
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

                // Pre-check: short-circuit before round-tripping to the client.
                if (preCheck is not null)
                {
                    string? block = preCheck(argsJson);
                    if (block is not null) return block;
                }

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
                string result;
                try
                {
                    result = await tcs.Task;
                    log.LogInformation("[Client] ← {Tool}  {Label}  callId={CallId}  bytes={Bytes}", name, label, callId, result.Length);
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

                // Post-hook: may modify or augment the result (e.g. appending a block warning).
                if (postHook is not null)
                {
                    string? modified = postHook(argsJson, result);
                    if (modified is not null) result = modified;
                }

                return result;
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
        WebSocket ws, LLMModule llm, ARI.LLM.Thread initialThread,
        string initialThreadKey, List<string> fileTree, ConnectionState state,
        FileToolState fileState, ILogger log)
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
                                    FileToolState reboundFileState = threadFileState.GetOrAdd(bindKey, _ => new FileToolState());
                                    RegisterTools(codeThread, ws, log, reboundFileState);
                                    threadKey = bindKey;
                                }
                            }

                            // Persistent context for the Code agent: a project map, plus any
                            // global coding conventions / per-project rules the client sends.
                            codeThread.ProjectMap = BuildProjectMap(fileTree);
                            // Global coding conventions come from the backend store (edited in the
                            // control panel); project rules come from the project's instructions.
                            string conventions = ConventionsStore.Get();
                            codeThread.CodingConventions = string.IsNullOrWhiteSpace(conventions) ? null : conventions.Trim();
                            if (doc.RootElement.TryGetProperty("projectRules", out var prEl))
                            {
                                string pr = prEl.GetString() ?? "";
                                codeThread.ProjectRules = string.IsNullOrWhiteSpace(pr) ? null : pr.Trim();
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

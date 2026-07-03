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

    // Which connection currently owns the registered client tools on a given thread. A stale connection's
    // cleanup must NOT strip tools a newer connection has re-registered on the same thread — that is how a
    // reconnect mid-turn left the architect with tools=0 and pushed it into the toolless parsing fallback.
    private static readonly ConcurrentDictionary<string, Guid> threadToolOwner = new();

    private sealed class FileToolState
    {
        // Guardrail scope of the AGENT this state belongs to. The architect's state is the root; each spawned
        // Coder gets a FRESH child scope (its context starts empty, so it must never inherit the parent's
        // "already read" ledger) linked back here so an edit made by one agent invalidates the other's ranges.
        public FileToolState? Parent { get; init; }

        // Files modified by write_file or edit_file this session. Must be re-read before further edits.
        public ConcurrentDictionary<string, byte> DirtyFiles   { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Consecutive edit_file failures per file. Reset on read_file.
        public ConcurrentDictionary<string, int>  EditFailures { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Line ranges already read per file, tagged with the user-turn serial they were read in. WHY: re-reading
        // a range the model already has in context re-injects thousands of UNCACHED tokens (a single big re-read
        // measured ~150s of prompt processing) and adds nothing — so a covered re-read is short-circuited with a
        // pointer. Scoped to the CURRENT turn only: content read in an earlier turn may have been condensed out
        // of the model's context, and "scroll up to that result" is a lie once it has been. Cleared when the
        // file is edited (line numbers shift, so a fresh read is legitimate).
        public ConcurrentDictionary<string, List<(int Start, int End, int Turn)>> ReadRanges { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Files previewed this session. preview_file must precede read_file (the model sees the line count
        // and outline, then picks a range) — the same gate ServerFileSystem enforces for local projects.
        public ConcurrentDictionary<string, byte> PreviewedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Raw pre-edit content per file, captured just before this scope's FIRST edit/write of that file —
        // the restore point revert_file writes back. Per-scope on purpose: a Coder reverts to the state the
        // file was in when ITS task began, not to before an earlier coder's completed work.
        public ConcurrentDictionary<string, string> Snapshots { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Line counts learned from preview_file, used to serve small files directly instead of forcing the
        // preview-then-re-read round-trip dance.
        public ConcurrentDictionary<string, int> KnownLineCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The file changed on disk: forget recorded read ranges and preview state for it in this
        /// scope AND every linked scope (parent chain) — their line numbers are stale now too.</summary>
        public void InvalidateFile(string path)
        {
            for (FileToolState? s = this; s is not null; s = s.Parent)
            {
                s.ReadRanges.TryRemove(path, out _);
                s.PreviewedFiles.TryRemove(path, out _);
                s.KnownLineCounts.TryRemove(path, out _);
            }
        }
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
        Guid connId  = Guid.NewGuid();

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

        RegisterTools(codeThread, ws, log, fileState, connId);

        try
        {
            log.LogInformation("[Client] Entering receive loop");
            await ReceiveLoop(ws, llm, codeThread, threadKey, fileTree, state, fileState, log, connId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Client] Receive loop crashed");
        }
        finally
        {
            UnregisterIfOwner(codeThread, connId, log);
            log.LogInformation("[Client] Session ended ({Thread})", threadKey);
        }
    }

    private static readonly string[] ClientToolNames =
        { "preview_file", "read_file", "list_directory", "search_files", "find_files", "edit_file", "write_file",
          "delete_file", "move_file", "run_command", "revert_file" };

    /// <summary>Removes this connection's client tools from the thread — but ONLY if this connection is still
    /// the registered owner. A stale (reconnected-over) connection closing late must not strip the tools the
    /// live connection just registered: that left the architect mid-turn with zero tools.</summary>
    private static void UnregisterIfOwner(ARI.LLM.Thread thread, Guid connId, ILogger log)
    {
        if (!threadToolOwner.TryGetValue(thread.Key, out Guid owner) || owner != connId)
        {
            log.LogInformation("[Client] Skipping tool unregister on {Thread} — a newer connection owns the tools.", thread.Key);
            return;
        }
        threadToolOwner.TryRemove(thread.Key, out _);
        thread.ClientToolCloner = null;
        foreach (string tool in ClientToolNames)
            thread.UnregisterTool(tool);
    }

    private static void RegisterTools(ARI.LLM.Thread thread, WebSocket ws, ILogger log, FileToolState fileState, Guid connId)
    {
        threadToolOwner[thread.Key] = connId;

        // Spawned Coder sub-threads get their OWN guardrail scope (fresh read/preview ledgers, own snapshots)
        // linked to this one, over the same socket. Without this a Coder inherits "already read" state for
        // content its empty context has never seen, gets every read blocked, and edits blind.
        thread.ClientToolCloner = child =>
        {
            RegisterCoderScopeTools(child, ws, log, new FileToolState { Parent = fileState }, thread);
            return true;
        };

        RegisterAgentTools(thread, ws, log, fileState, epochThread: thread, coderScope: false);
    }

    /// <summary>The lean executor toolset for a spawned Coder (no search/list/find/run — those make a think-off
    /// Coder wander), with its own guardrail scope. Turn epochs come from the PARENT thread (the user turn).</summary>
    private static void RegisterCoderScopeTools(ARI.LLM.Thread child, WebSocket ws, ILogger log, FileToolState childState, ARI.LLM.Thread epochThread)
        => RegisterAgentTools(child, ws, log, childState, epochThread, coderScope: true);

    private static void RegisterAgentTools(ARI.LLM.Thread thread, WebSocket ws, ILogger log, FileToolState fileState, ARI.LLM.Thread epochThread, bool coderScope)
    {
        if (!coderScope)
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
            labelField: "path",
            postHook: (argsJson, result) => { MarkPreviewed(argsJson, result, fileState); return null; });

        RegisterTool(thread, ws, log,
            name: "read_file",
            description: "Read a file from the user's project. Lines come back numbered so you can edit_file by line number afterwards. HARD LIMIT: at most 100 lines per call — wider requests are rejected without being read. ALWAYS call preview_file on a file BEFORE your first read_file on it — preview shows the line count and outline so you pick the right range. Pass start_line and end_line spanning at most 100 lines, and use search_files to locate the range. To cover a longer stretch, read consecutive 100-line windows (1-100, then 101-200, ...) — they stack in your context as one continuous view. You rarely need to re-read a file you already have, or to re-read after editing (edit_file returns the updated lines around your change).",
            parameters: new { type = "object", properties = new {
                path       = new { type = "string",  description = "File path relative to project root" },
                start_line = new { type = "integer", description = "First line to read (1-based, inclusive). Omit to read from the start." },
                end_line   = new { type = "integer", description = "Last line to read (1-based, inclusive). Omit to read to the end." }
            }, required = new[] { "path" } },
            displayVerb: "Reading", displayDoneVerb: "Read",
            labelField: "path",
            // Card label carries the line range ("File.cs (101-200)") so consecutive window reads of the
            // same file are visibly distinct rather than looking like duplicate reads (#113).
            customDisplay:     argsJson => ReadCardMarker(argsJson, "start"),
            customDisplayDone: argsJson => ReadCardMarker(argsJson, "end"),
            preCheck: argsJson => CheckRedundantRead(argsJson, fileState, epochThread)
                                  ?? CheckReadWindow(argsJson, fileState),
            postHook: (argsJson, result) => ReadPostHook(argsJson, result, fileState, epochThread),
            divert:   argsJson => PreviewBeforeRead(argsJson, ws, log, fileState));

        if (!coderScope)
        {
            RegisterTool(thread, ws, log,
                name: "list_directory",
                description: "List files and subdirectories at a path within the project.",
                parameters: new { type = "object", properties = new { path = new { type = "string", description = "Directory path relative to project root. Defaults to root." } }, required = Array.Empty<string>() },
                displayVerb: "Listing directory", displayDoneVerb: "Listed directory",
                labelField: "path");

            RegisterTool(thread, ws, log,
                name: "search_files",
                description: "Search file contents with a regular expression. Returns each match as 'path:line: text' — the line numbers let you edit_file directly WITHOUT reading the whole file. Case-sensitive by default; set ignore_case for a case-insensitive search. Use this to find every call site / definition before changing a symbol.",
                parameters: new { type = "object", properties = new { pattern = new { type = "string", description = "Regular expression to search for, e.g. 'GrantAccess\\(' or 'class\\s+Token'." }, path = new { type = "string", description = "Directory to search in, relative to project root." }, glob = new { type = "string", description = "File filter e.g. '*.cs'. Defaults to all files." }, ignore_case = new { type = "boolean", description = "Set true for a case-insensitive match. Defaults to false." } }, required = new[] { "pattern" } },
                displayVerb: "Searching", displayDoneVerb: "Searched",
                labelField: "pattern");
        }

        RegisterTool(thread, ws, log,
            name: "edit_file",
            description: "Edit a file by replacing one or more line ranges with new text. REQUIREMENT: read_file the file first — you edit BY LINE NUMBER using the 1-based line numbers shown by read_file/search_files. Set start_line and end_line (inclusive) and new_string (the replacement text only; an empty string deletes the range). You never retype existing code — you point at the lines you can already see. To change several places at once, pass an 'edits' array of {start_line, end_line, new_string}; they all resolve against the file as you last read it, so line numbers don't shift between them. Batch every change to one file into a single call's 'edits' array — that way you don't have to re-read between edits. After a successful edit you get back the updated, re-numbered lines around your change; read those instead of re-reading the file to verify it landed. Use write_file for a new file or a full rewrite.",
            parameters: new { type = "object", properties = new {
                path       = new { type = "string",  description = "File path relative to project root" },
                start_line = new { type = "integer", description = "First line to replace (1-based, inclusive, exactly as shown by read_file/search_files)." },
                end_line   = new { type = "integer", description = "Last line to replace (1-based, inclusive). Defaults to start_line for a single line." },
                new_string = new { type = "string",  description = "Replacement text for the line range — the replacement code only, without the read_file line-number prefix. Empty string deletes the range." },
                edits      = new { type = "array",   description = "Batch several changes to this file at once; each item is {start_line, end_line, new_string}. They resolve against the file as you last read it and apply together." }
            }, required = new[] { "path" } },
            displayVerb: "Editing", displayDoneVerb: "Edited",
            labelField: "path",
            customDisplay: argsJson =>
            {
                string label = EditLabel(argsJson); // extracted leniently so the filename survives any later failure
                try
                {
                    using var doc = JsonDocument.Parse(argsJson);
                    (int added, int removed) = EditCounts(doc.RootElement);
                    return $"<!--ari-tool-start:edit_file:{label}|+{added}|-{removed}-->";
                }
                catch { return $"<!--ari-tool-start:edit_file:{label}-->"; }
            },
            customDisplayDone: argsJson =>
            {
                string label = EditLabel(argsJson);
                try
                {
                    using var doc = JsonDocument.Parse(argsJson);
                    string newStr  = doc.RootElement.TryGetProperty("new_string", out var ne) ? ne.GetString() ?? "" : "";
                    (int added, int removed) = EditCounts(doc.RootElement);
                    string patch   = BuildPatch(newStr);
                    string encoded = patch.Length is > 0 and <= 10_000
                        ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(patch))
                        : "";
                    return $"<!--ari-tool-end:edit_file:{label}|+{added}|-{removed}|{encoded}-->";
                }
                catch { return $"<!--ari-tool-end:edit_file:{label}|+0|-0-->"; }
            },
            preCheck: argsJson => CheckDirty(argsJson, fileState),
            preForward: argsJson => EnsureSnapshot(argsJson, ws, log, fileState),
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
            preForward: argsJson => EnsureSnapshot(argsJson, ws, log, fileState),
            postHook: (argsJson, result) => { MarkWriteDirty(argsJson, result, fileState); return null; });

        if (!coderScope)
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

        // revert_file — restore a file to the snapshot captured before this agent's first edit of it. Fully
        // server-side (the snapshot is written back via the client's write_file); the client never sees a
        // "revert_file" op. The Coder's prompts and the loop-guard nudges direct the model here when an edit
        // leaves a file broken — before this registration existed, that advice dead-ended in "unknown tool"
        // and the model shipped the broken file as success.
        RegisterTool(thread, ws, log,
            name: "revert_file",
            description: "Restore a file to its pre-edit snapshot (taken automatically before your first edit of it this task). Use when an edit has left the file broken and a single tight follow-up edit cannot fix it: revert, re-read, then redo the edit from scratch.",
            parameters: new { type = "object", properties = new { path = new { type = "string", description = "File path relative to project root." } }, required = new[] { "path" } },
            displayVerb: "Reverting", displayDoneVerb: "Reverted",
            labelField: "path",
            divert: argsJson => RevertFromSnapshot(argsJson, ws, log, fileState));
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
    /// On failure: increments counter; after 2 failures appends a hard block message.
    /// </summary>
    private static string? TrackEditResult(string argsJson, string result, FileToolState fileState)
    {
        string path = ExtractToolPath(argsJson);
        if (string.IsNullOrEmpty(path)) return null;

        bool isEditFailure = result.Contains("out of range", StringComparison.OrdinalIgnoreCase)
                          || result.Contains("requires start_line", StringComparison.OrdinalIgnoreCase)
                          || result.Contains("No changes made", StringComparison.OrdinalIgnoreCase);

        if (isEditFailure)
        {
            int count = fileState.EditFailures.AddOrUpdate(path, 1, (_, c) => c + 1);
            if (count >= 2)
                return result + $"\n\n[BLOCKED] edit_file has failed {count} times on '{path}'. You MUST call read_file on this file to get its current line numbers before attempting any further edits to it.";
        }
        else if (!result.StartsWith("[Error:", StringComparison.OrdinalIgnoreCase))
        {
            // Successful edit — mark dirty, reset failure counter, and forget prior read ranges in THIS scope
            // and every linked scope (an edit by a Coder stales the architect's ranges too): the edit shifted
            // line numbers, so a fresh read is legitimate (not a redundant re-read).
            fileState.EditFailures.TryRemove(path, out _);
            fileState.DirtyFiles.TryAdd(path, 0);
            fileState.InvalidateFile(path);
        }

        return null;
    }

    /// <summary>On successful write_file, marks the file as dirty and forgets prior read ranges everywhere.</summary>
    private static void MarkWriteDirty(string argsJson, string result, FileToolState fileState)
    {
        string path = ExtractToolPath(argsJson);
        if (!string.IsNullOrEmpty(path) && result.StartsWith("Successfully", StringComparison.OrdinalIgnoreCase))
        {
            fileState.DirtyFiles.TryAdd(path, 0);
            fileState.InvalidateFile(path);
        }
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

    /// <summary>
    /// Soft dedup for read_file. If the requested range is already fully covered by a range read earlier
    /// IN THE SAME USER TURN (and the file has NOT been edited since — line numbers still valid),
    /// short-circuits with a pointer instead of re-sending the bytes. WHY: a covered re-read re-injects
    /// uncached tokens (a big re-read measured ~150s of prompt processing) and tells the model nothing new.
    /// Scoped to the current turn because earlier turns' reads may have been condensed out of the model's
    /// context — pointing at content that is no longer there wedges the agent. This nudges, it does not ban:
    /// the model can still read a different/wider range, and any read is allowed after an edit.
    /// </summary>
    private static string? CheckRedundantRead(string argsJson, FileToolState fileState, ARI.LLM.Thread epochThread)
    {
        string path = ExtractToolPath(argsJson);
        if (string.IsNullOrEmpty(path)) return null;
        if (fileState.DirtyFiles.ContainsKey(path)) return null; // edited since — a fresh read is legitimate
        if (!fileState.ReadRanges.TryGetValue(path, out List<(int Start, int End, int Turn)>? ranges) || ranges is null) return null;

        int turn = epochThread.TurnSerial;
        (int reqStart, int reqEnd) = ARI.LLM.ReadFile.ExtractRange(argsJson);
        lock (ranges)
        {
            foreach ((int s, int e, int t) in ranges)
            {
                if (t == turn && s <= reqStart && e >= reqEnd)
                {
                    string seen = e == int.MaxValue ? $"from line {s} to the end" : $"lines {s}-{e}";
                    return $"[Already read] You already read {seen} of '{path}' earlier THIS turn — scroll up to that result rather than re-reading it. If you need a different part, read a different range; if you just edited it, read again for fresh line numbers.";
                }
            }
        }
        return null;
    }

    /// <summary>read_file card marker whose label includes the requested line range — "File.cs (101-200)" —
    /// so chained window reads of one file each show as their own distinct card (#113).</summary>
    private static string ReadCardMarker(string argsJson, string markerType)
    {
        try
        {
            string file = System.IO.Path.GetFileName(ExtractToolPath(argsJson).Trim('"', '\'', ' ', '\\'))
                .Replace("--", "&#45;&#45;");
            if (string.IsNullOrWhiteSpace(file)) file = "file";
            (int start, int end) = ReadFile.ExtractRange(argsJson);
            string range = start == 1 && end == int.MaxValue ? ""
                         : end == int.MaxValue               ? $" ({start}-end)"
                         :                                     $" ({start}-{end})";
            return $"<!--ari-tool-{markerType}:read_file:{file}{range}-->";
        }
        catch { return $"<!--ari-tool-{markerType}:read_file:file-->"; }
    }

    /// <summary>Rejects an oversized read_file BEFORE the websocket round-trip — the client never ships
    /// bytes the model shouldn't receive. Policy and messages live in <see cref="ARI.LLM.ReadFile"/>
    /// (shared with the local ServerFileSystem path); this just supplies the remote-side facts: the line
    /// count learned from the file's preview (0 = never previewed) and whether it was previewed at all
    /// (un-previewed files fall through to the <see cref="PreviewBeforeRead"/> divert, which answers with
    /// the outline, not the body).</summary>
    private static string? CheckReadWindow(string argsJson, FileToolState fileState)
    {
        string path = ExtractToolPath(argsJson);
        fileState.KnownLineCounts.TryGetValue(path, out int known);
        return ARI.LLM.ReadFile.CheckWindow(argsJson, path, known, fileState.PreviewedFiles.ContainsKey(path));
    }

    // Files at/under this many lines are served directly on an un-previewed read_file — a full read of a
    // small file is cheaper than the preview-plus-second-read round-trip the divert used to force (in
    // practice the model repeated the identical no-range read anyway, paying BOTH costs for every file).
    // Equal to the read window so the direct serve can never exceed it.
    private const int DirectReadLines = ARI.LLM.ReadFile.WindowLines;

    /// <summary>
    /// Preview-before-read for LARGE files: keeps context lean by forcing the model to see the line count
    /// and outline (and pick a range) before pulling content — the same gate ServerFileSystem enforces for
    /// local projects. Rather than reject the call and make the model re-issue preview_file then read_file
    /// (a wasted round-trip), we auto-divert: run preview_file on the client for it, and return that outline
    /// with a note telling it to now read a specific range. Small files (≤ DirectReadLines) are NOT diverted —
    /// the read proceeds immediately. Returns null (no divert) once the file has been previewed, when a
    /// specific range was requested, or if the preview itself fails — the read then proceeds normally.
    /// </summary>
    private static async Task<string?> PreviewBeforeRead(string argsJson, WebSocket ws, ILogger log, FileToolState fileState)
    {
        string path = ExtractToolPath(argsJson);
        if (string.IsNullOrEmpty(path) || fileState.PreviewedFiles.ContainsKey(path)) return null;

        // A targeted range read is exactly what the gate exists to encourage — let it through.
        (int reqStart, int reqEnd) = ARI.LLM.ReadFile.ExtractRange(argsJson);
        bool ranged = !(reqStart == 1 && reqEnd == int.MaxValue);
        if (ranged) return null;

        string outline = await Forward(ws, log, "preview_file", JsonSerializer.Serialize(new { path }), "path");
        if (!outline.StartsWith("[preview:")) return null;

        fileState.PreviewedFiles.TryAdd(path, 0);

        // Small file → serve the whole-file read directly (no second round-trip, no note to re-issue).
        var lc = System.Text.RegularExpressions.Regex.Match(outline, @"—\s*(\d+)\s+lines");
        if (lc.Success && int.TryParse(lc.Groups[1].Value, out int lines))
        {
            fileState.KnownLineCounts[path] = lines;
            if (lines <= DirectReadLines) return null;
        }

        return $"{outline}\n\n[Note: you called read_file on {path} before previewing it, so the preview " +
               $"is shown above. Now call read_file on {path} with start_line/end_line (at most " +
               $"{ReadFile.WindowLines} lines per call) to read only the section you need; consecutive windows " +
               $"stack in your context as one continuous view.]";
    }

    /// <summary>On successful preview_file, marks the file previewed so read_file on it is allowed.</summary>
    private static void MarkPreviewed(string argsJson, string result, FileToolState fileState)
    {
        string path = ExtractToolPath(argsJson);
        if (!string.IsNullOrEmpty(path) && result.StartsWith("[preview:"))
            fileState.PreviewedFiles.TryAdd(path, 0);
    }

    /// <summary>Records a successfully read range (tagged with the current user turn) so a later covered
    /// re-read can be short-circuited within that same turn.</summary>
    private static void RecordRead(string argsJson, FileToolState fileState, ARI.LLM.Thread epochThread)
    {
        string path = ExtractToolPath(argsJson);
        if (string.IsNullOrEmpty(path)) return;
        (int start, int end) = ARI.LLM.ReadFile.ExtractRange(argsJson);
        List<(int Start, int End, int Turn)> ranges = fileState.ReadRanges.GetOrAdd(path, _ => new List<(int, int, int)>());
        lock (ranges) ranges.Add((start, end, epochThread.TurnSerial));
    }

    // Upper bound on a single read_file result entering the model's context (~6k tokens). WHY: one oversized
    // read dumps thousands of UNCACHED tokens into context (a ~6k-token read measured ~150s of prompt
    // processing). Bounding each read keeps every step cheap; the model reads the next window or searches.
    private const int MaxReadChars = 24000;

    /// <summary>
    /// read_file post-processing: clear the dirty/failure flags, cap an oversized result so a single read
    /// can't blow the per-step context cost, and record the range for dedup. A capped read is NOT recorded —
    /// the model only received part of it, so a follow-up read of the rest must not be blocked.
    /// </summary>
    private static string? ReadPostHook(string argsJson, string result, FileToolState fileState, ARI.LLM.Thread epochThread)
    {
        ClearDirtyAndFailures(argsJson, fileState);

        if (result.Length <= MaxReadChars)
        {
            RecordRead(argsJson, fileState, epochThread);
            return null; // unchanged
        }

        // Truncate on a line boundary near the cap so numbered lines stay intact, then close the code fence.
        int cut = result.LastIndexOf('\n', Math.Min(MaxReadChars, result.Length - 1));
        if (cut <= 0) cut = Math.Min(MaxReadChars, result.Length);
        return result[..cut] +
               "\n```\n[Truncated to keep context lean — this read was large. Read a narrower range with start_line/end_line, or use search_files to jump straight to what you need.]";
    }

    /// <summary>
    /// Captures a raw pre-edit snapshot of the file before this scope's FIRST edit/write of it, by reading
    /// the full file from the client and stripping the line-number formatting. This is what makes revert_file
    /// real on remote projects — the file lives on the client, so the only restore point is one we take
    /// ourselves before mutating. Invisible to the model (the extra read never enters its context). A file
    /// that can't be read/parsed (e.g. a brand-new file) just gets no snapshot; revert_file then explains that.
    /// </summary>
    private static async Task EnsureSnapshot(string argsJson, WebSocket ws, ILogger log, FileToolState fileState)
    {
        string path = ExtractToolPath(argsJson);
        if (string.IsNullOrEmpty(path) || fileState.Snapshots.ContainsKey(path)) return;
        try
        {
            string readResult = await Forward(ws, log, "read_file", JsonSerializer.Serialize(new { path }), "path");
            string? raw = TryExtractRawContent(readResult);
            if (raw is not null)
            {
                fileState.Snapshots[path] = raw;
                log.LogInformation("[Client] Snapshot captured for {Path} ({Bytes} bytes) before first edit.", path, raw.Length);
            }
            else
            {
                log.LogWarning("[Client] Could not capture pre-edit snapshot for {Path} — revert_file will be unavailable for it.", path);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[Client] Snapshot capture failed for {Path}.", path);
        }
    }

    /// <summary>Recovers the raw file text from a client read_file result (the fenced, line-numbered block).
    /// Returns null when the result doesn't look like a full successful read.</summary>
    private static string? TryExtractRawContent(string readResult)
    {
        if (readResult.StartsWith("[Error", StringComparison.Ordinal)) return null;
        int fenceStart = readResult.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart < 0) return null;
        int bodyStart = readResult.IndexOf('\n', fenceStart);
        if (bodyStart < 0) return null;
        int fenceEnd = readResult.LastIndexOf("\n```", StringComparison.Ordinal);
        if (fenceEnd <= bodyStart) return null;

        string[] lines = readResult[(bodyStart + 1)..fenceEnd].Split('\n');
        var sb = new StringBuilder();
        var numbered = new System.Text.RegularExpressions.Regex(@"^\s{0,7}\d+: ?");
        int matched = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var m = numbered.Match(lines[i]);
            if (m.Success) matched++;
            if (i > 0) sb.Append('\n');
            sb.Append(m.Success ? lines[i][m.Length..] : lines[i]);
        }
        // Require the overwhelming majority of lines to carry the number prefix — otherwise this isn't the
        // format we think it is and a "revert" would write garbage.
        return matched >= lines.Length * 0.9 ? sb.ToString() : null;
    }

    /// <summary>revert_file implementation: writes the scope's pre-edit snapshot back via the client's
    /// write_file, then clears the dirty/failure/read state so the agent re-reads clean line numbers.</summary>
    private static async Task<string?> RevertFromSnapshot(string argsJson, WebSocket ws, ILogger log, FileToolState fileState)
    {
        string path = ExtractToolPath(argsJson);
        if (string.IsNullOrEmpty(path))
            return "[Error: revert_file requires a non-empty 'path'.]";
        if (!fileState.Snapshots.TryGetValue(path, out string? snapshot))
            return $"[Error: no pre-edit snapshot exists for '{path}' in this task — either it has not been " +
                   "edited here, or the snapshot could not be captured. Read the file and repair it with " +
                   "edit_file instead, or report the file's state honestly in your summary.]";

        string result = await Forward(ws, log, "write_file", JsonSerializer.Serialize(new { path, content = snapshot }), "path");
        if (!result.StartsWith("Successfully", StringComparison.OrdinalIgnoreCase))
            return $"[Error: revert of '{path}' failed — {result}]";

        // The file changed on disk again — stay dirty so the next edit is forced through a fresh read.
        fileState.EditFailures.TryRemove(path, out _);
        fileState.DirtyFiles.TryAdd(path, 0);
        fileState.InvalidateFile(path);
        int lineCount = snapshot.Length == 0 ? 0 : snapshot.Split('\n').Length;
        return $"Reverted '{path}' to its pre-edit snapshot ({lineCount} lines). All edits made this task are " +
               "undone. Read the range you need, then redo the change from scratch.";
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

    /// <summary>
    /// Extracts the file label for an edit_file card directly from the args text (regex), so the filename
    /// is shown even if the full JSON can't be parsed / a count step throws. Falls back to "file".
    /// </summary>
    private static string EditLabel(string argsJson)
    {
        var m = System.Text.RegularExpressions.Regex.Match(argsJson, "\"path\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
        if (!m.Success) return "file";
        string path = m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"").Replace("\\/", "/");
        try
        {
            string f = System.IO.Path.GetFileName(path.Trim());
            return string.IsNullOrEmpty(f) ? "file" : f.Replace("--", "&#45;&#45;");
        }
        catch { return "file"; }
    }

    /// <summary>
    /// Computes the +added / -removed line counts for an edit_file tool card. Edits are line-range
    /// anchored (start_line/end_line + new_string), single or via a MultiEdit 'edits' array.
    /// </summary>
    private static (int Added, int Removed) EditCounts(JsonElement root)
    {
        static int Lines(string s) => s.Length == 0 ? 0 : s.Split('\n').Length;

        if (root.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
        {
            int a = 0, r = 0;
            foreach (var e in edits.EnumerateArray())
            {
                a += Lines(e.TryGetProperty("new_string", out var n) ? n.GetString() ?? "" : "");
                if (e.TryGetProperty("start_line", out var sl) && sl.TryGetInt32(out int s) &&
                    e.TryGetProperty("end_line",   out var el) && el.TryGetInt32(out int en) && en >= s)
                    r += en - s + 1;
            }
            return (a, r);
        }

        int added = Lines(root.TryGetProperty("new_string", out var ns) ? ns.GetString() ?? "" : "");
        int removed = 0;
        if (root.TryGetProperty("start_line", out var s0) && s0.TryGetInt32(out int start) &&
            root.TryGetProperty("end_line",   out var e0) && e0.TryGetInt32(out int end) && end >= start)
            removed = end - start + 1;
        return (added, removed);
    }

    private static string BuildPatch(string newStr)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in newStr.Split('\n'))
            sb.Append('+').AppendLine(line);
        return sb.ToString();
    }

    // The string arguments each tool cannot be dispatched without. Guards against a mis-parsed fallback
    // call reaching the client with an empty path — the client then operates on the project ROOT (observed:
    // read_file with no path → EISDIR on the project directory).
    private static readonly Dictionary<string, string[]> RequiredToolArgs = new(StringComparer.Ordinal)
    {
        ["preview_file"] = new[] { "path" },
        ["read_file"]    = new[] { "path" },
        ["edit_file"]    = new[] { "path" },
        ["write_file"]   = new[] { "path" },
        ["delete_file"]  = new[] { "path" },
        ["revert_file"]  = new[] { "path" },
        ["move_file"]    = new[] { "source", "destination" },
        ["search_files"] = new[] { "pattern" },
        ["find_files"]   = new[] { "pattern" },
        ["run_command"]  = new[] { "command" },
    };

    private static string? CheckRequiredArgs(string name, string argsJson)
    {
        if (!RequiredToolArgs.TryGetValue(name, out string[]? required)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            foreach (string field in required)
            {
                string? v = doc.RootElement.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString() : null;
                if (string.IsNullOrWhiteSpace(v))
                    return $"[Error: {name} requires a non-empty '{field}' argument — the call was not sent to the client. Re-issue the call with '{field}' set.]";
            }
            return null;
        }
        catch
        {
            return $"[Error: {name} arguments were not valid JSON — the call was not sent to the client. Re-issue the call in the documented format.]";
        }
    }

    private static void RegisterTool(
        ARI.LLM.Thread thread, WebSocket ws, ILogger log,
        string name, string description, object parameters,
        string displayVerb, string displayDoneVerb, string labelField,
        Func<string, string>? customDisplay     = null,
        Func<string, string>? customDisplayDone = null,
        Func<string, string?>? preCheck         = null,
        Func<string, string, string?>? postHook = null,
        Func<string, Task<string?>>? divert     = null,
        Func<string, Task>? preForward          = null)
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
        // show and animate line counts as new_string / content arrive token-by-token.
        Func<string, string?>? streamingDisplayFn = (name is "edit_file" or "write_file")
            ? partialJson =>
            {
                string path = PartialJsonExtractString(partialJson, "path");
                if (string.IsNullOrEmpty(path)) return null; // wait until path is known
                string label = System.IO.Path.GetFileName(path.Trim('"', '\'', ' ', '\\')).Replace("--", "&#45;&#45;");
                if (name == "edit_file")
                {
                    int added = PartialJsonCountNewlines(partialJson, "new_string");
                    return $"<!--ari-tool-start:edit_file:{label}|+{added}-->";
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

                // Malformed/missing required args never reach the client (an empty path would make it
                // operate on the project root).
                if (CheckRequiredArgs(name, argsJson) is { } argErr) return argErr;

                // Pre-check: short-circuit before round-tripping to the client.
                if (preCheck is not null)
                {
                    string? block = preCheck(argsJson);
                    if (block is not null) return block;
                }

                // Divert: may answer the call with a DIFFERENT client round-trip (e.g. read_file on an
                // un-previewed file returns the preview instead — see PreviewBeforeRead; revert_file is
                // answered entirely by its divert).
                if (divert is not null)
                {
                    string? diverted = await divert(argsJson);
                    if (diverted is not null) return diverted;
                }

                // Pre-forward side effects (e.g. capturing a pre-edit snapshot for revert_file).
                if (preForward is not null) await preForward(argsJson);

                string result = await Forward(ws, log, name, argsJson, labelField);

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
    /// <see cref="ARI.LLM.FileSystem"/> backed by a connected desktop CLIENT over the WebSocket: each op is
    /// forwarded to the client, which performs it on ITS machine and returns the result. The transport mirrors
    /// the one the desktop's own file tools use (see <see cref="RegisterTool"/>) — the existing tools are left
    /// untouched; this is exposed so the architect/Coder file tools can later run over a client machine the
    /// same way they run over the server's disk (<see cref="ARI.LLM.ServerFileSystem"/>).
    /// </summary>
    internal sealed class ClientFileSystem : ARI.LLM.FileSystem
    {
        private readonly WebSocket ws;
        private readonly ILogger   log;
        internal ClientFileSystem(WebSocket ws, ILogger log) { this.ws = ws; this.log = log; }

        public override Task<string> Read(string a)    => Forward("read_file", a);
        public override Task<string> Preview(string a) => Forward("preview_file", a);
        public override Task<string> Edit(string a)    => Forward("edit_file", a);
        public override Task<string> Write(string a)   => Forward("write_file", a);
        public override Task<string> Search(string a)  => Forward("search_files", a);
        public override Task<string> Find(string a)    => Forward("find_files", a);
        public override Task<string> List(string a)    => Forward("list_directory", a);
        public override Task<string> Delete(string a)  => Forward("delete_file", a);
        public override Task<string> Move(string a)    => Forward("move_file", a);
        public override Task<string> Run(string a)     => Forward("run_command", a);

        private static string LabelField(string tool) => tool switch
        {
            "search_files" or "find_files" => "pattern",
            "move_file"                    => "source",
            "run_command"                  => "command",
            _                              => "path"
        };

        private Task<string> Forward(string toolName, string argsJson)
            => ClientWebSocket.Forward(ws, log, toolName, NormalizePathArg(argsJson), LabelField(toolName));
    }

    /// <summary>Sends one tool op to the connected client and awaits its reply — the callId/pendingFileCalls/
    /// timeout machinery shared by the registered thread tools, <see cref="ClientFileSystem"/>, and the
    /// preview-before-read divert.</summary>
    private static async Task<string> Forward(WebSocket ws, ILogger log, string toolName, string argsJson, string labelField)
    {
        // Fail fast on a dead socket instead of silently dropping the send and burning the full timeout —
        // the model gets an actionable error it can retry after the client's auto-reconnect (~seconds).
        if (ws.State != WebSocketState.Open)
        {
            log.LogWarning("[Client] → {Tool} SKIPPED — client socket is {State}.", toolName, ws.State);
            return $"[Error: the desktop client is disconnected, so {toolName} could not run. The client " +
                   "usually reconnects within a few seconds — retry the call once, and if it still fails, " +
                   "stop and tell the user the client connection was lost.]";
        }

        string callId = Guid.NewGuid().ToString("N");
        string label  = ExtractLogLabel(argsJson, labelField);
        var tcs = new TaskCompletionSource<string>();
        pendingFileCalls[callId]  = tcs;
        pendingCallLabels[callId] = label;

        log.LogInformation("[Client] → {Tool}  {Label}  callId={CallId}", toolName, label, callId);
        await Send(ws, new { type = toolName, callId, args = argsJson });

        // run_command can wait on a build/test plus user confirmation; write/edit round-trips
        // on large files are slower than reads.
        int timeoutSeconds = toolName switch
        {
            "run_command"               => 900,
            "write_file" or "edit_file" => 90,
            _                           => 30
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() => tcs.TrySetCanceled());
        try
        {
            string result = await tcs.Task;
            log.LogInformation("[Client] ← {Tool}  {Label}  callId={CallId}  bytes={Bytes}", toolName, label, callId, result.Length);
            return result;
        }
        catch (OperationCanceledException)
        {
            log.LogWarning("[Client] ← {Tool} TIMEOUT  {Label}  callId={CallId}", toolName, label, callId);
            return $"[Error: client did not respond to {toolName} within {timeoutSeconds}s]";
        }
        finally
        {
            pendingFileCalls.TryRemove(callId, out _);
            pendingCallLabels.TryRemove(callId, out _);
        }
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
        FileToolState fileState, ILogger log, Guid connId)
    {
        ARI.LLM.Thread codeThread = initialThread;
        string         threadKey  = initialThreadKey;
        var buffer = new byte[64 * 1024];

        try { await Inner(); }
        finally
        {
            UnregisterIfOwner(codeThread, connId, log);
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
                                    UnregisterIfOwner(codeThread, connId, log);
                                    codeThread = llm.GetOrCreateCodeThread(bindKey);
                                    FileToolState reboundFileState = threadFileState.GetOrAdd(bindKey, _ => new FileToolState());
                                    RegisterTools(codeThread, ws, log, reboundFileState, connId);
                                    threadKey = bindKey;
                                }
                            }

                            string conventions = ConventionsStore.Get();
                            string? projectRules = doc.RootElement.TryGetProperty("projectRules", out var prEl) ? prEl.GetString()?.Trim() : null;
                            llm.SetCodeThreadContext(
                                threadKey,
                                projectMap:   BuildProjectMap(fileTree),
                                conventions:  string.IsNullOrWhiteSpace(conventions) ? null : conventions.Trim(),
                                rules:        string.IsNullOrWhiteSpace(projectRules) ? null : projectRules);

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

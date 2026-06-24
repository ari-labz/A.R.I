using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

// Code-specific tool-loop behaviour. The generic LLM request/stream/execute/append loop lives in Agent;
// everything here is exercised ONLY by tool-bearing Code threads, so per the project's OOP rules it belongs
// on the subclass rather than the base. Agent calls these as virtual hooks (see Agent.cs).
internal partial class Code
{
    // After this many reads of the SAME file in one turn, every further read is handed back with a firm
    // "stop verifying" directive. Set above a legitimate preview+few-ranges+one-post-edit-check (~4-5).
    private const int READ_LOOP_CEILING        = 6;
    // Total search_files calls per turn (reset on edit, since line numbers shift) before every further
    // search is bounced with a "stop verifying in circles" directive. Above a legit exploration pass (~6-8).
    private const int SEARCH_LOOP_CEILING      = 9;
    // Stop the turn after this many "[omitted]" placeholder edit/write refusals — the copy-forward loop.
    private const int MAX_PLACEHOLDER_REFUSALS = 3;

    // ── Per-turn tool-loop state (created fresh per SendPrompt turn) ──────────────
    private sealed class CodeTurnState : ToolTurnState
    {
        public readonly Dictionary<string, int> ReadCounts          = new(StringComparer.OrdinalIgnoreCase);
        // Per-file read tally for the WHOLE turn, NOT cleared on edit (unlike ReadCounts) — drives the
        // re-read-loop circuit breaker.
        public readonly Dictionary<string, int> FileReadTotals      = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string>         EditedFiles         = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string>         EarlyEditAbortedOnce = new(StringComparer.OrdinalIgnoreCase);
        public          int                     BuildState;          // 0 unknown, 1 ok, 2 failed
        public readonly Dictionary<string, int> EditFailStreak      = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> WriteCounts         = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (string Result, int Count)> CommandCache = new(StringComparer.Ordinal);
        public readonly HashSet<string>         TurnEditPaths       = new(StringComparer.OrdinalIgnoreCase);
        public          bool                    TodoNudged;
        public          int                     PlaceholderRefusals;
        public readonly Dictionary<string, (int Index, string CallId)> LiveReads = new(StringComparer.OrdinalIgnoreCase);
        public          int                     NoProgressBatches;
        public readonly HashSet<string>         EditedPathsThisBatch = new(StringComparer.OrdinalIgnoreCase);
        // search_files dedup + loop guard. Keyed by normalized pattern; SearchTotal is the per-turn count.
        // Both reset on a successful edit (line numbers shift → re-searching is legitimate again).
        public readonly Dictionary<string, int> SearchCounts        = new(StringComparer.Ordinal);
        public          int                     SearchTotal;
    }

    // ── Static helpers (only meaningful for the tool loop) ───────────────────────
    private static string NormKey(string p) => System.IO.Path.GetFileName(p.Trim('"', '\'', ' ', '\\'));
    // Dedup key for run_command: the extracted command text with whitespace collapsed. Prefixed "cmd:" so
    // these entries can be cleared on an edit (a rebuild after a fix must re-run, not dedup).
    private static string NormCmd(string argsJson) => "cmd:" + Regex.Replace(
        (ToolCallParser.TryExtractJsonString(argsJson, "command") ?? argsJson).Trim(), @"\s+", " ");
    // Dedup key for search_files: the normalized regex pattern. Prefixed "search:" so it can be cleared on edit.
    private static string NormSearch(string argsJson) => "search:" + Regex.Replace(
        (ToolCallParser.TryExtractJsonString(argsJson, "pattern") ?? "").Trim('"', '\'', ' '), @"\s+", " ");
    private static bool IsBuildCmd(string c) => Regex.IsMatch(c,
        @"(?i)\b(dotnet\s+(build|publish|msbuild)|msbuild|make|cargo\s+build|go\s+build|npm\s+run\s+build|yarn\s+build|tsc)\b");
    private static bool IsTestCmd(string c) => Regex.IsMatch(c,
        @"(?i)\b(dotnet\s+(test|vstest)|vstest|cargo\s+test|go\s+test|pytest|npm\s+(run\s+)?test|yarn\s+test|jest)\b");
    private static string? CondenseBuildErrors(string output)
    {
        MatchCollection ms = Regex.Matches(output, @"(?im)^.*?:\s*error\s+[A-Za-z]+\d+:.*$");
        if (ms.Count == 0) return null;
        List<string> seen = new();
        foreach (Match m in ms)
        {
            string line = m.Value.Trim();
            string key  = Regex.Replace(line, @"\s*\[[^\]]*\]\s*$", "");
            if (!seen.Contains(key)) seen.Add(key);
        }
        int total = seen.Count;
        StringBuilder sb = new();
        sb.AppendLine($"Build failed with {total} error{(total == 1 ? "" : "s")}{(total > 10 ? " (showing the first 10)" : "")}:");
        foreach (string e in seen.Take(10)) sb.AppendLine(e);
        if (total > 10) sb.AppendLine($"... and {total - 10} more error(s). Fix the errors above (they list the file and line), then rebuild.");
        else            sb.AppendLine("Fix the errors above (they list the file and line), then rebuild.");
        return sb.ToString().TrimEnd();
    }
    private static void StubRead(List<object> messages, int index, string callId, string path)
    {
        if (index < 0 || index >= messages.Count) return;
        messages[index] = new
        {
            role         = "tool",
            tool_call_id = callId,
            name         = "read_file",
            content      = $"[An earlier copy of {path} was removed here to save context. If it was superseded by a later read, that newer copy is below — work from it. If you edited this file, its line numbers changed: read it once more before editing it again.]"
        };
    }

    // ── Hook overrides ───────────────────────────────────────────────────────────
    protected override ToolTurnState CreateToolTurnState() => new CodeTurnState();

    protected override void OnToolBatchStart(ToolTurnState state)
        => ((CodeTurnState)state).EditedPathsThisBatch.Clear();

    protected override string? StreamEditPrecheck(Thread thread, ToolTurnState stateBase, string toolName, string argsJson,
        IEnumerable<string> pendingReadPaths, List<Attachment> threadAtts, List<Attachment> msgAtts)
    {
        if (toolName != "edit_file") return null;
        CodeTurnState state = (CodeTurnState)stateBase;
        string? editPath = ToolCallParser.TryExtractJsonString(argsJson, "path");
        if (editPath is null) return null;
        string ekey = NormKey(editPath);
        bool readThisTurn = state.ReadCounts.ContainsKey(ekey)
            || state.EditedFiles.Contains(ekey)
            || threadAtts.Any(a => NormKey(a.Name) == ekey)
            || msgAtts.Any(a => NormKey(a.Name) == ekey);
        bool readInBatch = pendingReadPaths.Any(rp => NormKey(rp) == ekey);
        if (!readThisTurn && !readInBatch && !state.EarlyEditAbortedOnce.Contains(ekey))
        {
            state.EarlyEditAbortedOnce.Add(ekey);
            Shared.Logger.LogWarning("[{Agent}] ({Thread}) Streaming abort: edit_file on unread file '{File}' — generation cancelled mid-stream.", Name, thread.Key, editPath);
            return $"[System: Aborted before the edit completed — you have not read {editPath} this turn, so you don't have its current line numbers and the edit would target the wrong lines. Call preview_file then read_file (with start_line/end_line) on {editPath} first, then edit it.]";
        }
        return null;
    }

    protected override string? PreToolGuard(Thread thread, ToolTurnState stateBase, string toolName, string callId, string argsJson)
    {
        CodeTurnState state = (CodeTurnState)stateBase;

        if (toolName == "preview_file")
        {
            try
            {
                using JsonDocument pdoc = JsonDocument.Parse(argsJson);
                string ppath = NormKey(pdoc.RootElement.GetProperty("path").GetString() ?? "");
                if (state.CommandCache.TryGetValue($"preview_file:{ppath}", out var cachedPreview))
                {
                    state.CommandCache[$"preview_file:{ppath}"] = (cachedPreview.Result, cachedPreview.Count + 1);
                    return cachedPreview.Count >= 2
                        ? $"[System: You have previewed {ppath} {cachedPreview.Count + 1} times this turn. Stop previewing it — use read_file with start_line/end_line to read the section you need.]"
                        : $"[System: You already previewed {ppath} this turn. Here is the cached outline — do not preview it again:\n\n{cachedPreview.Result}]";
                }
            }
            catch { /* ignore */ }
        }

        if (toolName == "read_file")
        {
            try
            {
                using JsonDocument rdoc = JsonDocument.Parse(argsJson);
                string rpath    = NormKey(rdoc.RootElement.GetProperty("path").GetString() ?? "");
                string startStr = rdoc.RootElement.TryGetProperty("start_line", out var rsl) ? rsl.GetRawText() : "0";
                string endStr   = rdoc.RootElement.TryGetProperty("end_line",   out var rel) ? rel.GetRawText() : "0";
                string rangeKey = $"{rpath}:{startStr}-{endStr}";
                state.ReadCounts.TryGetValue(rangeKey, out int rc);
                state.ReadCounts[rangeKey] = rc + 1;
                state.ReadCounts.TryGetValue(rpath, out int pathRc);
                state.ReadCounts[rpath] = pathRc + 1;
                state.FileReadTotals.TryGetValue(rpath, out int fileTotalRc);
                state.FileReadTotals[rpath] = fileTotalRc + 1;
                state.BlindFirstRead = pathRc == 0 && !state.CommandCache.ContainsKey($"preview_file:{rpath}");
                if (rc >= 1)
                {
                    string cachedRead = state.CommandCache.TryGetValue($"read_file:{rangeKey}", out var rcv) ? rcv.Result : "";
                    if (state.FileReadTotals[rpath] >= READ_LOOP_CEILING)
                        return $"[System: You have read {rpath} {state.FileReadTotals[rpath]} times this turn and are looping. The content has not changed and re-reading will not help. To finish a multi-spot change, call search_files for the symbol (it returns exact current line numbers for every remaining call site), then edit_file each one — do not keep re-reading. If the change is already done, stop and give your summary now.]";
                    return string.IsNullOrEmpty(cachedRead)
                        ? $"[System: You already have {rpath} lines {startStr}–{endStr} in the context above. This is not a cache error — the content has not changed. Stop re-reading and make your edits now with edit_file (start_line/end_line). To see a different part, read a different range.]"
                        : $"[System: You already have these lines — this is not a cache error, the file has not changed. Stop re-reading and make your edits now with edit_file. Repeating the content for reference:]\n\n{cachedRead}";
                }
            }
            catch { /* ignore */ }
        }

        if (toolName == "search_files")
        {
            string skey = NormSearch(argsJson);
            state.SearchTotal++;
            state.SearchCounts.TryGetValue(skey, out int sc);
            state.SearchCounts[skey] = sc + 1;
            if (sc >= 1)
            {
                string cachedSearch = state.CommandCache.TryGetValue(skey, out var scv) ? scv.Result : "";
                return string.IsNullOrEmpty(cachedSearch)
                    ? "[System: You already ran this exact search this turn — its results are in the context above and the files have not changed. Don't repeat the same search; act on what you found, or read the specific lines you need.]"
                    : $"[System: You already searched for this exact pattern this turn — repeating the results; do not run it again:\n\n{cachedSearch}]";
            }
            if (state.SearchTotal >= SEARCH_LOOP_CEILING)
                return $"[System: You have run {state.SearchTotal} searches this turn — more than the task needs, and a sign you are verifying in circles. You already have enough to act. Stop searching: make your next edit now, or if the change is complete, stop and write your summary. (To confirm one symbol exists, read its definition once — don't keep searching.)]";
        }

        if (toolName == "edit_file")
        {
            string? editPath = null;
            try
            {
                using JsonDocument edoc = JsonDocument.Parse(argsJson);
                editPath = (edoc.RootElement.GetProperty("path").GetString() ?? "").Trim('"', '\'', ' ', '\\');
            }
            catch { /* fall through */ }
            if (!string.IsNullOrEmpty(editPath))
            {
                string ekey = NormKey(editPath);
                if (state.EditedPathsThisBatch.Contains(ekey))
                    return $"[System: edit_file was already applied to {editPath} earlier in this same batch of tool calls. This second edit was NOT applied — its line numbers were computed against the file's previous content and would now target the wrong lines. Re-read {editPath}, then make the next edit.]";
                state.EditedPathsThisBatch.Add(ekey);
            }
        }

        if (!state.TodoNudged && !HasTasks(thread) && toolName is "edit_file" or "write_file")
        {
            string? tp = null;
            try
            {
                using JsonDocument tdoc = JsonDocument.Parse(argsJson);
                tp = NormKey(tdoc.RootElement.GetProperty("path").GetString() ?? "");
            }
            catch { /* skip the nudge */ }
            if (!string.IsNullOrEmpty(tp) && state.TurnEditPaths.Count >= 1 && !state.TurnEditPaths.Contains(tp))
            {
                state.TodoNudged = true;
                return $"[System: You are now changing a second file ({tp}) but have no task checklist. Before this edit, call update_todos with the full plan — one item per file/change, and include updating call sites, tests, and building as their own items. Then make this edit. Maintaining the checklist is required for multi-file work.]";
            }
            if (!string.IsNullOrEmpty(tp)) state.TurnEditPaths.Add(tp);
        }
        else if (toolName is "edit_file" or "write_file")
        {
            try
            {
                using JsonDocument tdoc = JsonDocument.Parse(argsJson);
                string tp = NormKey(tdoc.RootElement.GetProperty("path").GetString() ?? "");
                if (!string.IsNullOrEmpty(tp)) state.TurnEditPaths.Add(tp);
            }
            catch { /* ignore */ }
        }

        if (toolName == "run_command")
        {
            if (state.BuildState != 1)
            {
                string cmdLine = ToolCallParser.TryExtractJsonString(argsJson, "command") ?? "";
                if (IsTestCmd(cmdLine) && !IsBuildCmd(cmdLine))
                {
                    Shared.Logger.LogInformation("[{Agent}] ({Thread}) blocked test before {State} build: {Cmd}", Name, thread.Key, state.BuildState == 2 ? "failed" : "successful", cmdLine);
                    return state.BuildState == 2
                        ? "[System: The build is currently failing — do not run tests yet. Fix the build errors first (run the build, resolve every reported error), then run the tests once it builds cleanly.]"
                        : "[System: Build before you test. Run the build first (e.g. 'dotnet build' on the project you changed) and confirm it reports no errors; only run tests if the build succeeds, otherwise you are testing stale binaries.]";
                }
            }

            string cmdKey = NormCmd(argsJson);
            if (state.CommandCache.TryGetValue(cmdKey, out var cached))
            {
                state.CommandCache[cmdKey] = (cached.Result, cached.Count + 1);
                return cached.Count >= 2
                    ? $"[System: You have run this exact command {cached.Count + 1} times this turn. Do not call it again — you already have the output. Use what you know to proceed or respond to the user.]"
                    : $"[System: You already ran this command earlier this turn. Here is the cached output — do not call it again:\n\n{cached.Result}]";
            }
        }

        return null;
    }

    protected override string PostToolProcess(Thread thread, ToolTurnState stateBase, string toolName, string argsJson, string result)
    {
        CodeTurnState state = (CodeTurnState)stateBase;

        if ((toolName is "edit_file" or "write_file")
            && result.StartsWith("Refused:", StringComparison.Ordinal)
            && result.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
            && ++state.PlaceholderRefusals >= MAX_PLACEHOLDER_REFUSALS)
            throw new LlmRequestFailedException(
                $"Stopped: {state.PlaceholderRefusals} edits in a row sent a redaction placeholder instead of real code — breaking the copy-forward loop. Any changes already applied are kept.");

        if (toolName == "read_file" && !ToolCallParser.IsError(result))
        {
            try
            {
                using JsonDocument rcDoc = JsonDocument.Parse(argsJson);
                string rcPath = NormKey(rcDoc.RootElement.GetProperty("path").GetString() ?? "");
                string rcS = rcDoc.RootElement.TryGetProperty("start_line", out var a2) ? a2.GetRawText() : "0";
                string rcE = rcDoc.RootElement.TryGetProperty("end_line",   out var b2) ? b2.GetRawText() : "0";
                state.CommandCache[$"read_file:{rcPath}:{rcS}-{rcE}"] = (result, 1);
            }
            catch { /* ignore */ }
        }

        if (toolName == "read_file" && state.BlindFirstRead
            && !result.StartsWith("[System:", StringComparison.OrdinalIgnoreCase)
            && !ToolCallParser.IsError(result))
            result += "\n[System: You read this file without previewing it first. For an unfamiliar file, call preview_file to see its structure and line numbers, then read only the range you need — and use search_files to locate specific call sites instead of scrolling.]";

        if (toolName == "read_file" && !ToolCallParser.IsError(result))
        {
            try
            {
                using JsonDocument brkDoc = JsonDocument.Parse(argsJson);
                string brkPath = NormKey(brkDoc.RootElement.GetProperty("path").GetString() ?? "");
                state.FileReadTotals.TryGetValue(brkPath, out int brkTotal);
                if (brkTotal >= READ_LOOP_CEILING)
                    result += $"\n[System: You have now read this file {brkTotal} times this turn — far more than the work needs. The file is fine: C# ignores indentation, so a brace at column 0 is still a valid, correctly-placed brace, and edit_file already showed you each change landed. Re-reading is not revealing a real problem. STOP re-reading and verifying. If one specific line is genuinely wrong, make a single tight edit to fix exactly that line; otherwise you are finished — write your summary now. Do not read this file again.]";
            }
            catch { /* ignore */ }
        }

        if (toolName == "run_command")
        {
            string cmdKeyW    = NormCmd(argsJson);
            string cmdTrimmed = (ToolCallParser.TryExtractJsonString(argsJson, "command") ?? "").Trim('"', '\'', ' ');
            if (Regex.IsMatch(cmdTrimmed, @"^\S+\.(csproj|sln|cs|fs|vb|py|ts|tsx|js|jsx|json|xml|yaml|yml|sh|ps1)$"))
                result = $"[System: \"{cmdTrimmed}\" is a filename, not a shell command — nothing was executed. Did you mean 'dotnet build {cmdTrimmed}', 'dotnet run --project {cmdTrimmed}', or similar?]";
            else
                state.CommandCache[cmdKeyW] = (result, 1);

            string cmdLine = ToolCallParser.TryExtractJsonString(argsJson, "command") ?? "";
            if (IsBuildCmd(cmdLine) || IsTestCmd(cmdLine))
            {
                bool failed = result.Contains("Build FAILED")
                    || result.Contains(": error ")
                    || Regex.IsMatch(result, @"\b[1-9]\d*\s+Error\(s\)");
                bool ok = !failed && (result.Contains("Build succeeded") || result.Contains("0 Error(s)"));
                if (ok)          state.BuildState = 1;
                else if (failed) state.BuildState = 2;

                if (state.BuildState == 2 && CondenseBuildErrors(result) is { } condensed)
                    result = condensed;
            }
        }

        if (toolName == "preview_file")
        {
            try
            {
                using JsonDocument pd2 = JsonDocument.Parse(argsJson);
                string pp = NormKey(pd2.RootElement.GetProperty("path").GetString() ?? "");
                state.CommandCache[$"preview_file:{pp}"] = (result, 1);
            }
            catch { /* ignore */ }
        }

        if (toolName == "search_files" && !ToolCallParser.IsError(result)
            && !result.StartsWith("[System:", StringComparison.Ordinal))
            state.CommandCache[NormSearch(argsJson)] = (result, 1);

        if (toolName == "edit_file")
        {
            try
            {
                using JsonDocument argDoc = JsonDocument.Parse(argsJson);
                string editPath = (argDoc.RootElement.GetProperty("path").GetString() ?? "").Trim('"', '\'', ' ');
                string editKey  = NormKey(editPath);
                bool edited = result.Contains("Successfully edited");
                if (edited)
                {
                    state.EditedFiles.Add(editKey);
                    state.EditFailStreak.Remove(editKey);
                }
                else
                {
                    state.EditFailStreak.TryGetValue(editKey, out int streak);
                    state.EditFailStreak[editKey] = ++streak;

                    bool environmental =
                        result.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                        result.Contains("File not found",    StringComparison.OrdinalIgnoreCase);

                    if (!environmental)
                    {
                        if (state.EditedFiles.Contains(editKey))
                            result += " This file was already edited earlier this turn — re-read it to see the current content and line numbers before retrying. If the file is in a broken state, use revert_file to restore it to its last clean snapshot, then re-read and plan the edit from scratch.";
                        else if (streak >= 2)
                            result += " Two edits in a row have failed on this file. Consider using revert_file to restore it to its last clean snapshot, then re-read and plan the edit again from scratch — that is faster and safer than continuing to patch a potentially inconsistent state.";
                    }
                    else if (streak >= 2)
                    {
                        result += " This is a filesystem permission/access problem, not an editing-strategy problem — stop retrying and tell the user the file cannot be written and why.";
                    }
                }
            }
            catch { /* ignore */ }
        }

        if (toolName == "write_file" && result.Contains("Successfully wrote"))
        {
            try
            {
                using JsonDocument argDoc = JsonDocument.Parse(argsJson);
                string writePath = NormKey(argDoc.RootElement.GetProperty("path").GetString() ?? "");
                state.EditFailStreak.Remove(writePath);
                state.EditedFiles.Add(writePath);
                state.WriteCounts.TryGetValue(writePath, out int wc);
                state.WriteCounts[writePath] = ++wc;
                if (wc == 2)
                    result += " You have already written this file this turn and that write succeeded. Do NOT write it again unless you have a further, distinct change. If you are unsure the content is correct, use read_file to verify — do not rewrite it blindly.";
                else if (wc >= 3)
                {
                    state.ForceNoMoreTools = true;
                    result += " This file has been written too many times this turn. No further tool calls will be accepted — tell the user the file has been updated and stop.";
                    Shared.Logger.LogWarning("[{Agent}] ({Thread}) write_file called {Count}x on '{File}' — cutting off tools for this turn.", Name, thread.Key, wc, writePath);
                }
            }
            catch { /* ignore */ }
        }

        return result;
    }

    protected override void AfterToolAppended(ToolTurnState stateBase, List<object> messages, string toolName, string callId, string argsJson, string result, int addedIndex)
    {
        CodeTurnState state = (CodeTurnState)stateBase;
        try
        {
            using JsonDocument hdoc = JsonDocument.Parse(argsJson);
            string hpath = hdoc.RootElement.TryGetProperty("path", out var hpe)
                ? (hpe.GetString() ?? "").Trim('"', '\'', ' ', '\\') : "";
            if (string.IsNullOrEmpty(hpath)) return;

            if (toolName == "read_file")
            {
                string startStr = hdoc.RootElement.TryGetProperty("start_line", out var sl) ? sl.GetRawText() : "0";
                string endStr   = hdoc.RootElement.TryGetProperty("end_line",   out var el) ? el.GetRawText() : "0";
                string rangeKey = $"{hpath}:{startStr}-{endStr}";
                if (state.LiveReads.TryGetValue(rangeKey, out var prev))
                    StubRead(messages, prev.Index, prev.CallId, hpath);
                if (!result.StartsWith("[System:"))
                    state.LiveReads[rangeKey] = (addedIndex, callId);
            }
            else if (toolName is "edit_file" or "write_file"
                     && (result.Contains("Successfully edited") || result.Contains("Successfully wrote")))
            {
                foreach (string k in state.LiveReads.Keys.Where(k => k.StartsWith(hpath + ":", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    StubRead(messages, state.LiveReads[k].Index, state.LiveReads[k].CallId, hpath);
                    state.LiveReads.Remove(k);
                }
                string editedKey = NormKey(hpath);
                foreach (string rk in state.ReadCounts.Keys.Where(k => k == editedKey || k.StartsWith(editedKey + ":", StringComparison.OrdinalIgnoreCase)).ToList())
                    state.ReadCounts.Remove(rk);
                foreach (string ck in state.CommandCache.Keys.Where(k => k.StartsWith($"read_file:{editedKey}:", StringComparison.OrdinalIgnoreCase) || k == $"preview_file:{editedKey}").ToList())
                    state.CommandCache.Remove(ck);
                foreach (string ck in state.CommandCache.Keys.Where(k => k.StartsWith("cmd:", StringComparison.Ordinal)).ToList())
                    state.CommandCache.Remove(ck);
                // Search results referenced pre-edit line numbers — drop them and reset the per-turn search
                // budget so the model can re-locate shifted call sites without tripping the loop guard.
                foreach (string ck in state.CommandCache.Keys.Where(k => k.StartsWith("search:", StringComparison.Ordinal)).ToList())
                    state.CommandCache.Remove(ck);
                state.SearchCounts.Clear();
                state.SearchTotal = 0;
            }
        }
        catch { /* ignore */ }
    }

    protected override bool OnBatchEndShouldBreak(Thread thread, ToolTurnState stateBase, bool productiveBatch)
    {
        CodeTurnState state = (CodeTurnState)stateBase;
        if (productiveBatch) { state.NoProgressBatches = 0; return false; }
        if (++state.NoProgressBatches >= 6)
        {
            Shared.Logger.LogWarning("[{Agent}] ({Thread}) loop-break: {N} consecutive tool batches made no progress — ending turn.", Name, thread.Key, state.NoProgressBatches);
            return true;
        }
        return false;
    }
}

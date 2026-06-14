using System.Text.Json;

namespace ARI.LLM;

internal sealed class EditFile : FileTool
{
    internal EditFile(string root, CancellationToken ct) : base(root, ct) { }

    internal override string Name => "edit_file";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "edit_file",
            description = "Make targeted find-and-replace edits to an existing file. For one change, pass old_string/new_string (old_string must match exactly once — add surrounding context to make it unique). To change several places at once, pass an 'edits' array of {old_string, new_string} objects — they are applied in order against one buffer, so use this instead of many separate edit_file calls when changing multiple call sites in the same file. Set replace_all (on a single edit or a batch item) to replace every occurrence of old_string. Use write_file for a new file or a full rewrite.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path       = new { type = "string", description = "File path relative to project root" },
                    old_string = new { type = "string", description = "The exact text to find. Must appear exactly once unless replace_all is set. Omit if using 'edits'." },
                    new_string = new { type = "string", description = "The text to replace it with. Omit if using 'edits'." },
                    replace_all = new { type = "boolean", description = "Replace every occurrence of old_string instead of requiring a unique match." },
                    edits      = new { type = "array", description = "Batch of edits applied in order: each item is {old_string, new_string, replace_all?}. Use instead of old_string/new_string for multiple changes to this file." }
                },
                required = new[] { "path" }
            }
        }
    };

    /// <summary>A single requested edit, normalized to LF space.</summary>
    private readonly record struct EditSpec(string Old, string New, bool ReplaceAll);

    internal override async Task<string> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            JsonElement rootEl = doc.RootElement;
            string relPath = (rootEl.GetProperty("path").GetString() ?? "").Trim('"', '\'', ' ');
            string? absPath = Resolve(relPath);
            if (absPath is null)
                return "Access denied: path traversal is not allowed.";
            if (!File.Exists(absPath))
                return $"File not found: {relPath}";

            // Normalize the request into a uniform list of edits. Supports a single old/new pair or a
            // MultiEdit-style 'edits' array applied sequentially against one buffer.
            List<EditSpec> edits = new();
            if (rootEl.TryGetProperty("edits", out JsonElement editsEl) && editsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in editsEl.EnumerateArray())
                    edits.Add(new EditSpec(
                        Normalize(e.TryGetProperty("old_string", out var o) ? o.GetString() ?? "" : ""),
                        Normalize(e.TryGetProperty("new_string", out var n) ? n.GetString() ?? "" : ""),
                        e.TryGetProperty("replace_all", out var r) && r.ValueKind == JsonValueKind.True));
            }
            else
            {
                edits.Add(new EditSpec(
                    Normalize(rootEl.TryGetProperty("old_string", out var o) ? o.GetString() ?? "" : ""),
                    Normalize(rootEl.TryGetProperty("new_string", out var n) ? n.GetString() ?? "" : ""),
                    rootEl.TryGetProperty("replace_all", out var r) && r.ValueKind == JsonValueKind.True));
            }
            if (edits.Count == 0)
                return $"No edits provided for {relPath}.";

            string content = await ReadWithRetry(absPath);
            string nl      = content.Contains("\r\n") ? "\r\n" : "\n";
            string buf     = Normalize(content);

            bool anyFuzzy = false; int replacements = 0, firstStart = -1, firstNewLen = 0;
            for (int i = 0; i < edits.Count; i++)
            {
                EditSpec ed = edits[i];
                string label = edits.Count > 1 ? $" (edit {i + 1} of {edits.Count})" : "";
                if (ed.Old.Length == 0)
                    return $"old_string is empty{label}. Provide the exact text to replace in {relPath}.";

                if (ed.ReplaceAll)
                {
                    if (!buf.Contains(ed.Old, StringComparison.Ordinal))
                        return $"old_string not found in {relPath}{label}. No changes made.{ClosestRegionHint(buf, ed.Old)}";
                    int at = buf.IndexOf(ed.Old, StringComparison.Ordinal);
                    int count = 0; for (int p = at; p >= 0; p = buf.IndexOf(ed.Old, p + ed.Old.Length, StringComparison.Ordinal)) count++;
                    buf = buf.Replace(ed.Old, ed.New);
                    replacements += count;
                    if (firstStart < 0) { firstStart = at; firstNewLen = ed.New.Length; }
                    continue;
                }

                MatchResult match = FindMatch(buf, ed.Old);
                if (match.Kind == MatchKind.Multiple)
                    return $"old_string matches {match.Count} locations in {relPath}{label}. Include more surrounding lines to make it unique, or set replace_all to change them all.";
                if (match.Kind == MatchKind.None)
                    return $"old_string not found in {relPath}{label}. No changes made.{ClosestRegionHint(buf, ed.Old)}";

                buf = buf[..match.Start] + ed.New + buf[(match.Start + match.Length)..];
                if (match.Kind == MatchKind.Whitespace) anyFuzzy = true;
                replacements++;
                if (firstStart < 0) { firstStart = match.Start; firstNewLen = ed.New.Length; }
            }

            await WriteWithRetry(absPath, buf.Replace("\n", nl));

            string[] lines = buf.Split('\n');
            string   note  = anyFuzzy ? " (matched ignoring indentation/line-ending differences)" : "";

            if (edits.Count > 1)
                return $"Successfully edited {relPath}.{note} Applied {edits.Count} edits ({replacements} replacements). File is now {lines.Length} lines.";

            // Single edit: numbered snippet around the change so the model sees the new state.
            int    editLine     = buf[..firstStart].Count(c => c == '\n');
            int    newLineCount = firstNewLen == 0 ? 1 : buf.Substring(firstStart, firstNewLen).Count(c => c == '\n') + 1;
            int    from         = Math.Max(0, editLine - 5);
            int    to           = Math.Min(lines.Length - 1, editLine + newLineCount + 4);
            string snippet      = string.Join("\n", lines[from..(to + 1)].Select((l, i) => $"{from + i + 1,6}: {l}"));
            string replNote     = replacements > 1 ? $" ({replacements} occurrences replaced)" : "";
            return $"Successfully edited {relPath}.{note}{replNote}\n\n[Updated context — lines {from + 1}–{to + 1}]\n```\n{snippet}\n```";
        }
        catch (Exception ex) { return $"Error editing file: {ex.Message}"; }
    }

    // Transient FS errors (sharing/lock races, indexers) clear in milliseconds — retry briefly
    // rather than surfacing a hard failure that makes the model abandon edit_file.
    private async Task<string> ReadWithRetry(string absPath)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await File.ReadAllTextAsync(absPath, ct); }
            catch (IOException) when (attempt < 3) { await Task.Delay(40 * (attempt + 1), ct); }
            catch (UnauthorizedAccessException) when (attempt < 3) { await Task.Delay(40 * (attempt + 1), ct); }
        }
    }

    private async Task WriteWithRetry(string absPath, string data)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { await File.WriteAllTextAsync(absPath, data, ct); return; }
            catch (IOException) when (attempt < 3) { await Task.Delay(40 * (attempt + 1), ct); }
            catch (UnauthorizedAccessException) when (attempt < 3) { await Task.Delay(40 * (attempt + 1), ct); }
        }
    }

    private enum MatchKind { Exact, Whitespace, None, Multiple }

    /// <summary>The outcome of locating old_string. Start/Length are a char span into normalized content.</summary>
    private readonly record struct MatchResult(MatchKind Kind, int Start, int Length, int Count);

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>
    /// Locates old_string in content, tolerating line-ending and indentation drift.
    /// Tier 1: exact substring. Tier 2: leading-whitespace-insensitive line-block match.
    /// A looser tier is only accepted when it resolves to exactly one region.
    /// </summary>
    private static MatchResult FindMatch(string content, string old)
    {
        // Tier 1 — exact (in normalized space).
        int count = 0, idx = 0, firstIdx = -1;
        while ((idx = content.IndexOf(old, idx, StringComparison.Ordinal)) >= 0)
        {
            if (firstIdx < 0) firstIdx = idx;
            count++; idx += old.Length;
        }
        if (count == 1) return new MatchResult(MatchKind.Exact, firstIdx, old.Length, 1);
        if (count > 1)  return new MatchResult(MatchKind.Multiple, -1, 0, count);

        // Tier 2 — leading-whitespace-insensitive, contiguous line-block match.
        string[] contentLines = content.Split('\n');
        string[] oldLines     = old.Split('\n');
        string[] oldTrim      = oldLines.Select(l => l.TrimStart()).ToArray();
        int      k            = oldLines.Length;
        if (k == 0 || k > contentLines.Length) return new MatchResult(MatchKind.None, -1, 0, 0);

        int matchStartLine = -1, matches = 0;
        for (int w = 0; w + k <= contentLines.Length; w++)
        {
            bool all = true;
            for (int i = 0; i < k; i++)
                if (!contentLines[w + i].TrimStart().Equals(oldTrim[i], StringComparison.Ordinal)) { all = false; break; }
            if (all) { matches++; if (matchStartLine < 0) matchStartLine = w; }
        }
        if (matches > 1) return new MatchResult(MatchKind.Multiple, -1, 0, matches);
        if (matches == 1)
        {
            int start = 0;
            for (int i = 0; i < matchStartLine; i++) start += contentLines[i].Length + 1; // +1 for the '\n'
            int len = 0;
            for (int i = 0; i < k; i++) len += contentLines[matchStartLine + i].Length + (i < k - 1 ? 1 : 0);
            return new MatchResult(MatchKind.Whitespace, start, len, 1);
        }

        return new MatchResult(MatchKind.None, -1, 0, 0);
    }

    /// <summary>
    /// When no match is found, return the file region most similar to old_string (by trimmed
    /// line equality) with line numbers, so the model can copy the exact bytes instead of guessing.
    /// </summary>
    private static string ClosestRegionHint(string content, string old)
    {
        string[] contentLines = content.Split('\n');
        string[] oldTrim      = old.Split('\n').Select(l => l.TrimStart()).ToArray();
        int      k            = Math.Min(oldTrim.Length, contentLines.Length);
        if (k == 0)
            return " Re-read the file to get the exact current text, then retry with a matching old_string.";

        int bestStart = -1, bestScore = -1;
        for (int w = 0; w + k <= contentLines.Length; w++)
        {
            int score = 0;
            for (int i = 0; i < k; i++)
                if (contentLines[w + i].TrimStart().Equals(oldTrim[i], StringComparison.Ordinal)) score++;
            if (score > bestScore) { bestScore = score; bestStart = w; }
        }

        if (bestScore <= 0 || bestStart < 0)
            return " Re-read the file to get the exact current text, then retry with a matching old_string.";

        int    to     = Math.Min(contentLines.Length - 1, bestStart + k - 1);
        string region = string.Join("\n", Enumerable.Range(bestStart, to - bestStart + 1)
            .Select(i => $"{i + 1,6}: {contentLines[i]}"));
        return $" The closest matching region is lines {bestStart + 1}–{to + 1}:\n```\n{region}\n```\n" +
               "Copy that text exactly (including indentation) into old_string if it is the code you meant to edit.";
    }

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string p = doc.RootElement.GetProperty("path").GetString() ?? "";
            return $"<div class=\"tool-use\">Editing {p.Replace("&", "&amp;").Replace("<", "&lt;")}</div>\n";
        }
        catch { return "<div class=\"tool-use\">Editing file</div>\n"; }
    };
}

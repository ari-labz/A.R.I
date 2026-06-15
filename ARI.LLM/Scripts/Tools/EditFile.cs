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
            description = "Edit a file by replacing one or more regions. REQUIREMENT: you MUST call read_file on a file before editing it — never edit a file you have not read in this session, as you will fabricate old_string content that does not exist and the edit will fail. PREFERRED: anchor by line range — set start_line/end_line (the 1-based inclusive line numbers shown by read_file/search_files) and new_string. This is reliable because you don't retype existing code: replace a block, or delete it with new_string empty (e.g. start_line 196, end_line 232). Alternatively anchor by text with old_string (must match exactly once unless replace_all). To change several places at once, pass an 'edits' array — each item is {start_line,end_line,new_string} (preferred) or {old_string,new_string}; all regions resolve against the file you just read and apply together, so line numbers don't shift between them. Use write_file for a new file or a full rewrite.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path       = new { type = "string",  description = "File path relative to project root" },
                    start_line = new { type = "integer", description = "PREFERRED. First line to replace (1-based inclusive, as shown by read_file/search_files)." },
                    end_line   = new { type = "integer", description = "Last line to replace (1-based inclusive). Defaults to start_line." },
                    new_string = new { type = "string",  description = "Replacement text for the line range or old_string. Empty string deletes the region." },
                    old_string = new { type = "string",  description = "Alternative to start_line/end_line: exact text to find (must match once unless replace_all). Only valid if you have read this file and are copying the text verbatim from the read result — never reconstruct it from memory." },
                    replace_all = new { type = "boolean", description = "Replace every occurrence of old_string instead of requiring a unique match." },
                    edits      = new { type = "array",   description = "Batch for several changes to this file; each item {start_line,end_line,new_string} (preferred) or {old_string,new_string}." }
                },
                required = new[] { "path" }
            }
        }
    };

    /// <summary>A requested edit: text-anchored (Old set) or line-anchored (StartLine set).</summary>
    private readonly record struct EditSpec(string Old, string New, bool ReplaceAll, int StartLine, int EndLine);
    /// <summary>A resolved character span to replace, against the original buffer.</summary>
    private readonly record struct Span(int Start, int Len, string Rep);

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

            // Normalize the request into a uniform list of edits (single, or a MultiEdit batch).
            static EditSpec Parse(JsonElement e) => new(
                Normalize(e.TryGetProperty("old_string", out var o) ? o.GetString() ?? "" : ""),
                Normalize(e.TryGetProperty("new_string", out var n) ? n.GetString() ?? "" : ""),
                e.TryGetProperty("replace_all", out var r) && r.ValueKind == JsonValueKind.True,
                e.TryGetProperty("start_line", out var s) && s.TryGetInt32(out int sv) ? sv : 0,
                e.TryGetProperty("end_line",   out var en) && en.TryGetInt32(out int ev) ? ev : 0);

            List<EditSpec> edits = new();
            if (rootEl.TryGetProperty("edits", out JsonElement editsEl) && editsEl.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in editsEl.EnumerateArray()) edits.Add(Parse(e));
            else
                edits.Add(Parse(rootEl));
            if (edits.Count == 0)
                return $"No edits provided for {relPath}.";

            string content = await ReadWithRetry(absPath);
            string nl      = content.Contains("\r\n") ? "\r\n" : "\n";
            string buf0    = Normalize(content);

            // Offsets where each line begins, so a 1-based line range maps to a character span.
            List<int> lineStarts = new() { 0 };
            for (int i = 0; i < buf0.Length; i++) if (buf0[i] == '\n') lineStarts.Add(i + 1);
            int totalLines = lineStarts.Count;

            // Resolve every edit to one or more character spans against the ORIGINAL buffer.
            List<Span> spans = new();
            bool anyFuzzy = false; bool multi = edits.Count > 1;
            for (int i = 0; i < edits.Count; i++)
            {
                EditSpec ed = edits[i];
                string label = multi ? $" (edit {i + 1} of {edits.Count})" : "";

                if (ed.StartLine > 0)
                {
                    int s = ed.StartLine, en = ed.EndLine > 0 ? ed.EndLine : ed.StartLine;
                    if (s < 1 || s > totalLines || en < s || en > totalLines)
                        return $"start_line/end_line {s}-{en} is out of range{label} — {relPath} has {totalLines} lines. Re-read the file for current line numbers.";
                    int offStart = lineStarts[s - 1];
                    int offEnd; bool hadNL;
                    if (en < totalLines) { offEnd = lineStarts[en]; hadNL = true; } else { offEnd = buf0.Length; hadNL = false; }
                    string rep = ed.New;
                    if (rep.Length > 0 && hadNL && !rep.EndsWith('\n')) rep += "\n";
                    spans.Add(new Span(offStart, offEnd - offStart, rep));
                    continue;
                }

                if (ed.Old.Length == 0)
                    return $"Provide old_string or start_line/end_line{label} to edit {relPath}.";

                if (ed.ReplaceAll)
                {
                    bool found = false;
                    for (int p = buf0.IndexOf(ed.Old, StringComparison.Ordinal); p >= 0; p = buf0.IndexOf(ed.Old, p + ed.Old.Length, StringComparison.Ordinal))
                    { spans.Add(new Span(p, ed.Old.Length, ed.New)); found = true; }
                    if (!found) return $"old_string not found in {relPath}{label}. No changes made.{ClosestRegionHint(buf0, ed.Old)}";
                    continue;
                }

                MatchResult match = FindMatch(buf0, ed.Old);
                if (match.Kind == MatchKind.Multiple)
                    return $"old_string matches {match.Count} locations in {relPath}{label}. Include more surrounding lines to make it unique, or set replace_all to change them all.";
                if (match.Kind == MatchKind.None)
                    return $"old_string not found in {relPath}{label}. No changes made.{ClosestRegionHint(buf0, ed.Old)}";
                if (match.Kind == MatchKind.Whitespace) anyFuzzy = true;
                spans.Add(new Span(match.Start, match.Length, ed.New));
            }

            // Overlap check, then apply highest-offset-first so earlier edits don't shift later ones.
            spans.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = 1; i < spans.Count; i++)
                if (spans[i].Start < spans[i - 1].Start + spans[i - 1].Len)
                    return $"Edits overlap in {relPath} — two edits target the same region. Combine them into one edit.";

            int firstStart = spans.Count > 0 ? spans[0].Start : 0;
            int firstRepLen = spans.Count > 0 ? spans[0].Rep.Length : 0;

            string buf = buf0;
            foreach (Span sp in spans.OrderByDescending(s => s.Start))
                buf = buf[..sp.Start] + sp.Rep + buf[(sp.Start + sp.Len)..];

            await WriteWithRetry(absPath, buf.Replace("\n", nl));

            string[] lines = buf.Split('\n');
            string   note  = anyFuzzy ? " (matched ignoring indentation/line-ending differences)" : "";

            if (multi)
                return $"Successfully edited {relPath}.{note} Applied {edits.Count} edits ({spans.Count} replacements). File is now {lines.Length} lines.";

            // Single edit: numbered snippet around the change so the model sees the new state.
            int    editLine     = buf[..firstStart].Count(c => c == '\n');
            int    newLineCount = firstRepLen == 0 ? 1 : buf.Substring(firstStart, firstRepLen).Count(c => c == '\n') + 1;
            int    from         = Math.Max(0, editLine - 5);
            int    to           = Math.Min(lines.Length - 1, editLine + newLineCount + 4);
            string snippet      = string.Join("\n", lines[from..(to + 1)].Select((l, i) => $"{from + i + 1,6}: {l}"));
            string replNote     = spans.Count > 1 ? $" ({spans.Count} occurrences replaced)" : "";
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
            return " None of the file resembles that old_string — don't retype it. Re-read the file, then change the lines using start_line/end_line (you can see the line numbers) instead of old_string.";

        int    to     = Math.Min(contentLines.Length - 1, bestStart + k - 1);
        string region = string.Join("\n", Enumerable.Range(bestStart, to - bestStart + 1)
            .Select(i => $"{i + 1,6}: {contentLines[i]}"));
        return $" The closest matching region is lines {bestStart + 1}–{to + 1}:\n```\n{region}\n```\n" +
               $"Either copy that text EXACTLY into old_string, or — simpler — edit by line number using start_line/end_line (e.g. start_line {bestStart + 1}).";
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

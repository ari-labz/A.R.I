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
            description = "Edit a file by line number. Make the SMALLEST edit that does the job: change ONLY the lines that actually differ. To rename a symbol on lines 8, 10 and 13, edit those three lines individually — do NOT replace the whole method, and never re-type surrounding code that isn't changing (that is how lines get dropped or duplicated). REQUIREMENT: call read_file first; you edit by the 1-based line numbers it shows. Set start_line and end_line (inclusive) and new_string (the replacement for exactly those lines). For a single changed line, start_line == end_line and new_string is that one line. To change several separate spots, pass an 'edits' array of {start_line,end_line,new_string} — one TIGHT item per spot; they resolve against the file as you last read it and apply together, so line numbers don't shift between them. An empty new_string deletes the range. Use write_file only for a brand-new file or a genuine whole-file rewrite — never to change a few lines.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path       = new { type = "string",  description = "File path relative to project root." },
                    start_line = new { type = "integer", description = "First line to replace (1-based inclusive, exactly as shown by read_file/search_files)." },
                    end_line   = new { type = "integer", description = "Last line to replace (1-based inclusive). Equals start_line for a single-line change — the common case." },
                    new_string = new { type = "string",  description = "Replacement for exactly the start_line..end_line range — the changed line(s) only, no read_file 'N|' prefix, and no unchanged surrounding lines. Empty string deletes the range." },
                    edits      = new { type = "array",   description = "Several tight changes at once; each item is {start_line, end_line, new_string}, kept as narrow as possible. They resolve against the file as you last read it and apply together. Prefer many small items over one wide range." }
                },
                required = new[] { "path" }
            }
        }
    };

    // A single edit_file edit may replace at most this many lines when new_string has content. Wider
    // replacements are bounced back so the model edits only the lines that change (or uses write_file for a
    // real rewrite) — re-typing a whole block to change a few lines is how this model drops/duplicates code.
    // Deletions (empty new_string) are exempt: removing a large range in one go is legitimate.
    private const int MAX_REPLACE_SPAN = 15;

    /// <summary>A requested edit, anchored by line range (1-based inclusive).</summary>
    private readonly record struct EditSpec(string New, int StartLine, int EndLine);
    /// <summary>A resolved character span to replace, against the original buffer.</summary>
    private readonly record struct Span(int Start, int Len, string Rep);

    /// <summary>
    /// Read a line-number property, tolerating the quotes/whitespace the model can wrap around it
    /// under the text protocol (e.g. "174"). Returns 0 when absent or unparseable.
    /// </summary>
    private static int ReadLine(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el)) return 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v)) return v;
        if (el.ValueKind == JsonValueKind.String)
        {
            string digits = new(((el.GetString() ?? "").Where(c => char.IsDigit(c) || c == '-')).ToArray());
            if (int.TryParse(digits, out int sv)) return sv;
        }
        return 0;
    }

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
                Normalize(e.TryGetProperty("new_string", out var n) ? n.GetString() ?? "" : ""),
                ReadLine(e, "start_line"),
                ReadLine(e, "end_line"));

            List<EditSpec> edits = new();
            if (rootEl.TryGetProperty("edits", out JsonElement editsEl) && editsEl.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in editsEl.EnumerateArray()) edits.Add(Parse(e));
            else
                edits.Add(Parse(rootEl));
            if (edits.Count == 0)
                return $"No edits provided for {relPath}.";
            // History compaction renders earlier payloads as "[omitted]"; never apply that placeholder.
            if (edits.Any(e => IsRedactionPlaceholder(e.New)))
                return $"Refused: a new_string was a placeholder (\"[omitted]\"), not real replacement text. That appears in the conversation only because an earlier payload was hidden to save space. Re-send the literal replacement text for {relPath}.";

            string content = await ReadWithRetry(absPath);
            string nl      = content.Contains("\r\n") ? "\r\n" : "\n";
            string buf0    = Normalize(content);

            // Offsets where each line begins, so a 1-based line range maps to a character span.
            List<int> lineStarts = new() { 0 };
            for (int i = 0; i < buf0.Length; i++) if (buf0[i] == '\n') lineStarts.Add(i + 1);
            int totalLines = lineStarts.Count;

            // Resolve every edit to a character span against the ORIGINAL buffer. Line-number anchoring
            // only — the model points at the lines it can already see from read_file/search_files.
            List<Span> spans = new();
            bool multi = edits.Count > 1;
            for (int i = 0; i < edits.Count; i++)
            {
                EditSpec ed = edits[i];
                string label = multi ? $" (edit {i + 1} of {edits.Count})" : "";

                if (ed.StartLine <= 0)
                    return $"edit_file requires start_line and end_line{label} — the 1-based line numbers shown by read_file/search_files. Re-read {relPath} if you don't have them, then set start_line, end_line (inclusive) and new_string (the replacement text only).";

                int s = ed.StartLine, en = ed.EndLine > 0 ? ed.EndLine : ed.StartLine;
                if (s < 1 || s > totalLines || en < s || en > totalLines)
                    return $"start_line/end_line {s}-{en} is out of range{label} — {relPath} has {totalLines} lines. Re-read the file for current line numbers.";
                if (en - s + 1 > MAX_REPLACE_SPAN && ed.New.Trim().Length > 0)
                    return $"This edit{label} replaces {en - s + 1} lines at once ({s}-{en}). Edit only the line(s) that actually change — pass an 'edits' array with one tight item per changed spot (start_line==end_line for a single line); don't re-type the surrounding unchanged code, it gets dropped. For a genuine whole-block rewrite use write_file. (Deleting a range with an empty new_string is allowed at any size.)";
                int offStart = lineStarts[s - 1];
                int offEnd; bool hadNL;
                if (en < totalLines) { offEnd = lineStarts[en]; hadNL = true; } else { offEnd = buf0.Length; hadNL = false; }
                string rep = ed.New;
                if (rep.Length > 0) rep = MatchIndent(rep, LeadingWhitespace(buf0, offStart));
                if (rep.Length > 0 && hadNL && !rep.EndsWith('\n')) rep += "\n";
                spans.Add(new Span(offStart, offEnd - offStart, rep));
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

            if (multi)
                return $"Successfully edited {relPath}. Applied {edits.Count} edits ({spans.Count} replacements). File is now {lines.Length} lines.";

            // Single edit: numbered snippet around the change so the model sees the new state.
            int    editLine     = buf[..firstStart].Count(c => c == '\n');
            int    newLineCount = firstRepLen == 0 ? 1 : buf.Substring(firstStart, firstRepLen).Count(c => c == '\n') + 1;
            int    from         = Math.Max(0, editLine - 5);
            int    to           = Math.Min(lines.Length - 1, editLine + newLineCount + 4);
            string snippet      = string.Join("\n", lines[from..(to + 1)].Select((l, i) => $"{from + i + 1,6}: {l}"));
            return $"Successfully edited {relPath}.\n\n[Updated context — lines {from + 1}–{to + 1}]\n```\n{snippet}\n```";
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

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>Leading run of spaces/tabs on the line that begins at <paramref name="offset"/>.</summary>
    private static string LeadingWhitespace(string buf, int offset)
    {
        int i = offset;
        while (i < buf.Length && (buf[i] == ' ' || buf[i] == '\t')) i++;
        return buf[offset..i];
    }

    /// <summary>
    /// Restores the leading indentation of the FIRST line of a replacement to match the line it replaces.
    /// The model reliably drops the indent of new_string's first line (the line-number tool means it never
    /// has to repeat the surrounding code, but it forgets the first line's own indent) while giving the
    /// remaining lines their correct absolute indentation. So we only ever fix the first line — a uniform
    /// shift would over-indent the already-correct lines below it. Indent is only added, never removed.
    /// </summary>
    private static string MatchIndent(string rep, string targetIndent)
    {
        if (targetIndent.Length == 0 || rep.Length == 0 || rep[0] == '\n') return rep;

        int firstIndent = 0;
        while (firstIndent < rep.Length && (rep[firstIndent] == ' ' || rep[firstIndent] == '\t')) firstIndent++;
        if (firstIndent >= targetIndent.Length) return rep;

        return targetIndent[firstIndent..] + rep;
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

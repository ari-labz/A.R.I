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
            description = "Edit a file by line number. Two distinct operations — pick the right one:\n• REPLACE existing lines: set start_line and end_line (1-based inclusive) and new_string. For one line, start_line == end_line. An empty new_string deletes the range.\n• INSERT brand-new lines (a new method, field, using, etc.) WITHOUT changing any existing line: set insert_after to the line the new code should follow, and new_string to the new line(s). The text is added on its own line(s) right after that line; NOTHING is replaced (insert_after:0 inserts at the very top of the file). This is the correct way to add code between existing blocks — do NOT 'replace' an adjacent line (like a closing brace or a blank line) just to squeeze new code in: that drops the line you replaced and is the #1 cause of broken edits.\nMake the SMALLEST edit that does the job: change ONLY the lines that actually differ, and never re-type surrounding code that isn't changing (that is how lines get dropped or duplicated). To rename a symbol on lines 8, 10 and 13, edit those three lines individually. REQUIREMENT: call read_file first; you edit by the 1-based line numbers it shows. To do several changes at once, pass an 'edits' array of items — each item is either a replace ({start_line,end_line,new_string}) or an insert ({insert_after,new_string}); they resolve against the file as you last read it and apply together, so line numbers don't shift between them. Use write_file only for a brand-new file or a genuine whole-file rewrite — never to change a few lines.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path         = new { type = "string",  description = "File path relative to project root." },
                    start_line   = new { type = "integer", description = "REPLACE mode: first line to replace (1-based inclusive, exactly as shown by read_file/search_files). Omit when inserting." },
                    end_line     = new { type = "integer", description = "REPLACE mode: last line to replace (1-based inclusive). Equals start_line for a single-line change — the common case. Omit when inserting." },
                    insert_after = new { type = "integer", description = "INSERT mode: add new_string on its own line(s) immediately AFTER this 1-based line, replacing nothing (0 = insert at the top of the file). Use this to add new code between existing lines instead of replacing an anchor line." },
                    new_string   = new { type = "string",  description = "The new/replacement line(s) only — no read_file 'N|' prefix, no unchanged surrounding lines. In REPLACE mode it replaces start_line..end_line (empty deletes the range); in INSERT mode it is the text inserted after insert_after." },
                    edits        = new { type = "array",   description = "Several changes at once; each item is a replace {start_line,end_line,new_string} OR an insert {insert_after,new_string}, kept as narrow as possible. They resolve against the file as you last read it and apply together. Prefer many small items over one wide range." }
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

    /// <summary>A requested edit. Either a REPLACE (StartLine..EndLine, 1-based inclusive) or, when
    /// InsertAfter >= 0, an INSERT of New on its own line(s) immediately after line InsertAfter (0 = top).</summary>
    private readonly record struct EditSpec(string New, int StartLine, int EndLine, int InsertAfter);
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

    /// <summary>Reads insert_after; returns -1 when absent (so 0 can mean "insert at the very top").</summary>
    private static int ReadInsertAfter(JsonElement parent)
    {
        if (!parent.TryGetProperty("insert_after", out JsonElement el)) return -1;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v)) return v;
        if (el.ValueKind == JsonValueKind.String)
        {
            string digits = new(((el.GetString() ?? "").Where(c => char.IsDigit(c) || c == '-')).ToArray());
            if (int.TryParse(digits, out int sv)) return sv;
        }
        return -1;
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
                ReadLine(e, "end_line"),
                ReadInsertAfter(e));

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

                // Idempotency guard: this model frequently re-applies an edit it already made (re-inserting a
                // method it already added → a duplicate definition that won't compile). If the new block is
                // already present in the file verbatim, refuse rather than duplicate it.
                if (ed.New.Trim().Length > 0 && BlockAlreadyPresent(buf0, ed.New, out int dupLine))
                    return $"Refused{label}: that block is already present in {relPath} (around line {dupLine}). "
                         + "You already added it earlier — applying it again would create a duplicate (e.g. a "
                         + "duplicate method, which won't compile). If you meant to CHANGE it, edit those existing "
                         + "lines with start_line/end_line; otherwise the file already has your change — move on.";

                // INSERT mode: add New on its own line(s) after line InsertAfter, replacing nothing.
                if (ed.InsertAfter >= 0)
                {
                    int after = ed.InsertAfter;
                    if (after > totalLines)
                        return $"insert_after {after} is past the end of {relPath}{label} — it has {totalLines} line(s). Re-read for current line numbers.";
                    int    insOff       = after >= totalLines ? buf0.Length : lineStarts[after];
                    int    anchorLineIdx= after >= 1 ? after - 1 : 0;
                    string anchorIndent = LeadingWhitespace(buf0, lineStarts[anchorLineIdx]);
                    string insRep       = ed.New;
                    if (insRep.Length > 0) insRep = MatchIndent(insRep, anchorIndent);
                    if (insRep.Length > 0 && !insRep.EndsWith('\n')) insRep += "\n";
                    // Inserting at EOF when the file has no trailing newline: separate from the last line.
                    if (insOff == buf0.Length && buf0.Length > 0 && !buf0.EndsWith('\n') && insRep.Length > 0)
                        insRep = "\n" + insRep;
                    spans.Add(new Span(insOff, 0, insRep));
                    continue;
                }

                if (ed.StartLine <= 0)
                    return $"edit_file requires start_line and end_line{label} (to replace lines) or insert_after (to add new lines) — the 1-based line numbers shown by read_file/search_files. Re-read {relPath} if you don't have them.";

                int s = ed.StartLine, en = ed.EndLine > 0 ? ed.EndLine : ed.StartLine;
                if (s < 1 || s > totalLines || en < s || en > totalLines)
                    return $"start_line/end_line {s}-{en} is out of range{label} — {relPath} has {totalLines} lines. Re-read the file for current line numbers.";

                // Replace-as-insert brace-drop guard. The model habitually "inserts" by replacing an anchor
                // line (start==end). When that line is a lone brace and the replacement is a multi-line block
                // that doesn't put the brace back, the brace is DELETED → broken structure, and the model's
                // fix-up then usually duplicates the block. Refuse and point at insert_after (the right tool).
                if (s == en && ed.New.Trim().Length > 0)
                {
                    int    lcEnd       = s < totalLines ? lineStarts[s] - 1 : buf0.Length;
                    string lineContent = buf0[lineStarts[s - 1]..lcEnd].Trim();
                    if (lineContent == "}" || lineContent == "{")
                    {
                        List<string> nb = ed.New.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                        bool preservesBrace = nb.Count > 0 && (nb[0] == lineContent || nb[^1] == lineContent);
                        if (nb.Count > 1 && !preservesBrace)
                            return $"Refused{label}: line {s} of {relPath} is just `{lineContent}`. Replacing it with {nb.Count} lines would DELETE that brace and break the structure — the #1 cause of broken edits here. To ADD code, use insert_after (nothing is replaced): insert_after={s} adds after line {s}, insert_after={s - 1} adds before it. Use start_line/end_line only to change the text of an existing line.";
                    }
                }

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

            // Brace-balance guard. If the file's { and } were balanced before and this edit unbalances them,
            // the edit dropped or added a brace (the recurring corruption: dropped closing brace, extra
            // closing brace, half-duplicated method) and the result won't compile. Refuse rather than write it.
            // Only acts when buf0 is balanced, so files with lone braces in string/char literals are left alone.
            int open0 = buf0.Count(c => c == '{'), close0 = buf0.Count(c => c == '}');
            int open1 = buf.Count(c => c == '{'),  close1 = buf.Count(c => c == '}');
            if (open0 == close0 && open1 != close1)
                return $"Refused: this edit would leave {relPath} with unbalanced braces ({open1} `{{` vs {close1} `}}`) — it was balanced before, so a brace was dropped or added and the file would not compile. Nothing was written. Check the braces in your new_string: add a COMPLETE block with matching `{{` and `}}` (use insert_after to add a whole method/class), and don't replace or omit an existing brace line.";

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

    /// <summary>
    /// True if <paramref name="rep"/> already exists in <paramref name="buf0"/> as a contiguous run of lines
    /// (comparing lines trimmed of leading/trailing whitespace, so indentation differences don't matter). Only
    /// fires for a substantial block (≥3 non-blank lines) to avoid flagging ordinary one/two-line edits.
    /// Catches the model re-inserting a method/block it already added — a duplicate that won't compile.
    /// </summary>
    private static bool BlockAlreadyPresent(string buf0, string rep, out int atLine)
    {
        atLine = 0;
        List<string> rl = rep.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
        while (rl.Count > 0 && rl[0].Length == 0)        rl.RemoveAt(0);
        while (rl.Count > 0 && rl[^1].Length == 0)       rl.RemoveAt(rl.Count - 1);
        if (rl.Count(l => l.Length > 0) < 3) return false;

        string needle = string.Join("\n", rl);
        string hay    = string.Join("\n", buf0.Replace("\r", "").Split('\n').Select(l => l.Trim()));
        int idx = hay.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0) return false;
        atLine = hay[..idx].Count(c => c == '\n') + 1;
        return true;
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

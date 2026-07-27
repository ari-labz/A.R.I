using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>
/// <see cref="FileSystem"/> backed by the SERVER's own disk at a root path — the eval / CLI today, and
/// server-stored projects later. Each override is the disk logic relocated verbatim from the matching
/// server-side file tool, so behaviour is unchanged. (Shared-logic de-duplication with ClientFileSystem
/// is a later step; for now each backend keeps its own exact behaviour.)
/// </summary>
internal sealed class ServerFileSystem : FileSystem
{
    private const int MAX_ENTRIES        = 200;   // list_directory recursive cap
    private const int MAX_RESULTS        = 200;   // find_files / search_files result cap
    private const int READ_MAX_LINES     = 800;   // whole-file read cap (legacy backstop behind ReadFile.CheckWindow)
    private const int READ_MAX_CHARS     = 48000;
    private const int SEARCH_MAX_CHARS   = 8000;
    private const int MAX_REPLACE_SPAN   = 15;    // edit_file: max lines a content replacement may span

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "dist", "build", ".next", ".nuxt",
        ".idea", ".vs", ".vscode", "coverage", "out", "target", "vendor", "packages",
        ".gradle", "Pods", "__pycache__", "Models", "Voices", "External"
    };

    private readonly string            root;
    private readonly CancellationToken ct;
    private readonly FileSnapshots?    gate;   // preview-before-read + edit/revert snapshots
    private readonly bool              brainVault; // vault root: redirect content/name search to search_brain

    // Redirect returned when a memory agent reaches for a raw-file search over the vault. The filesystem
    // search (regex over text / glob over filenames) can't see a note's aliases; search_brain resolves by
    // title, alias, and content through the index. Told, not blocked — so the model self-corrects.
    private const string BrainSearchRedirect =
        "To search the brain, use the search_brain tool instead — it searches note titles, aliases, and " +
        "content through the memory index. Give it plain words (a name or phrase), not a regex or glob.";

    public ServerFileSystem(string root, CancellationToken ct, FileSnapshots? gate = null, bool brainVault = false)
    {
        this.root       = root;
        this.ct         = ct;
        this.gate       = gate;
        this.brainVault = brainVault;
    }

    /// <summary>Resolves a project-relative path to an absolute one, or null if it escapes the root.</summary>
    private string? Resolve(string relPath)
    {
        string absPath = Path.GetFullPath(Path.Combine(root, relPath));
        return absPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? absPath : null;
    }

    /// <summary>True if the text is one of ARI's history-redaction placeholders.</summary>
    private static bool IsRedactionPlaceholder(string? s)
    {
        string t = (s ?? "").Trim();
        return t is "[content omitted]" or "[omitted]";
    }

    // Models emit line numbers as quoted strings ("275") under the text tool protocol; GetInt32() throws
    // on a String element. Cast tolerantly — accept a number or a numeric string, error only if neither.
    private static bool TryGetLineArg(JsonElement el, out int value)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt32(out value);
        if (el.ValueKind == JsonValueKind.String) return int.TryParse(el.GetString()?.Trim('"', '\'', ' '), out value);
        value = 0;
        return false;
    }

    // ── read_file (from ReadFile.cs) ───────────────────────────────────────────────────────────────
    public override async Task<string> Read(string argsJson)
    {
        try
        {
            using JsonDocument doc     = JsonDocument.Parse(argsJson);
            string             relPath = (doc.RootElement.GetProperty("path").GetString() ?? string.Empty).Trim('"', '\'', ' ');
            string?            absPath = Resolve(relPath);

            if (absPath is null)        return "Access denied: path traversal is not allowed.";
            if (!File.Exists(absPath))  return $"File not found: {relPath}";
            // Preview-before-read: keeps context lean by forcing the model to see the line count and the
            // large-file warning (and pick a range) before pulling content. Rather than reject the call and
            // make the model re-issue preview_file then read_file (a wasted round-trip), we auto-divert: run
            // preview_file for it (which marks the file previewed), and return that outline with a note telling
            // it why the call was diverted and to now read the specific range it needs.
            if (gate is not null && !gate.WasPreviewed(absPath))
            {
                // Return the preview outline FIRST (result starts with "[preview:", not "[System:", so the loop
                // renders it as a normal preview — Agent relabels the read card to "Previewing" — not an error card).
                string outline = await Preview(argsJson);   // reads the same "path" arg; marks the file previewed
                return $"{outline}\n\n[Note: previewing {relPath} for you first. This outline usually gives you everything " +
                       $"you need to USE the type — exact fields, properties and method signatures. Only call read_file with a " +
                       $"start_line/end_line for a specific method if you genuinely must see how it behaves inside; otherwise " +
                       $"work from this outline and move on.]";
            }

            string[] lines      = await File.ReadAllLinesAsync(absPath, ct);
            int      totalLines = lines.Length;

            bool hasStart = doc.RootElement.TryGetProperty("start_line", out JsonElement startEl);
            bool hasEnd   = doc.RootElement.TryGetProperty("end_line",   out JsonElement endEl);
            int startLine = 1, endLine = totalLines;
            if (hasStart && !TryGetLineArg(startEl, out startLine))
                return $"Error reading file: start_line must be an integer (got '{startEl}').";
            if (hasEnd && !TryGetLineArg(endEl, out endLine))
                return $"Error reading file: end_line must be an integer (got '{endEl}').";

            startLine = Math.Max(1,         Math.Min(startLine, totalLines));
            endLine   = Math.Max(startLine, Math.Min(endLine,   totalLines));

            // Redundant/overlap re-read dedup — shared policy with the remote path (see FileSnapshots).
            if (gate?.RedundantRead(absPath, startLine, endLine) is { } dupNudge)
                return dupNudge;

            // Hard per-call read window — shared policy with the remote path (see ReadFile.CheckWindow).
            // previewed: true — the preview gate above has already diverted un-previewed reads.
            if (ReadFile.CheckWindow(argsJson, relPath, totalLines, previewed: true) is { } windowErr)
                return windowErr;

            // Cap whole-file reads so a single read can't blow the context window.
            // Targeted reads (start_line/end_line supplied) are not capped — the caller chose the range.
            bool capped = false;
            if (!hasStart && !hasEnd && totalLines > 0)
            {
                int chars = 0, lim = totalLines;
                for (int i = 0; i < totalLines; i++)
                {
                    chars += lines[i].Length + 1;
                    if (i + 1 >= READ_MAX_LINES || chars >= READ_MAX_CHARS) { lim = i + 1; break; }
                }
                if (lim < totalLines) { endLine = lim; capped = true; }
            }

            bool   fullFile = startLine == 1 && endLine == totalLines;
            string header   = fullFile
                ? $"[file: \"{relPath}\" ({totalLines} lines)]"
                : $"[file: \"{relPath}\" lines {startLine}-{endLine} of {totalLines}]";

            StringBuilder sb = new();
            sb.AppendLine(header);
            sb.AppendLine("```");
            for (int i = startLine - 1; i < endLine; i++)
                sb.AppendLine($"{i + 1}|{lines[i]}");
            sb.Append("```");
            if (capped)
                sb.Append($"\n[Large file ({totalLines} lines total) — capped at line {endLine}. Use search_files to find the relevant lines, then re-read with start_line/end_line.]");
            else if (!hasStart && !hasEnd && totalLines > 150)
                sb.Append($"\n[Tip: this file has {totalLines} lines. For future reads, use search_files to locate content first, then read only the relevant range with start_line/end_line.]");
            // Record what the model actually received, for dedup. A capped read isn't recorded — the model
            // only got part of it, so a follow-up read of the rest must not be blocked.
            if (!capped) gate?.RecordRead(absPath, startLine, endLine);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    // ── preview_file (from PreviewFile.cs) ─────────────────────────────────────────────────────────
    public override async Task<string> Preview(string argsJson)
    {
        try
        {
            using JsonDocument doc     = JsonDocument.Parse(argsJson);
            string             relPath = (doc.RootElement.GetProperty("path").GetString() ?? "").Trim('"', '\'', ' ');
            string?            absPath = Resolve(relPath);

            if (absPath is null)       return "Access denied: path traversal is not allowed.";
            if (!File.Exists(absPath)) return $"File not found: {relPath}";
            gate?.MarkPreviewed(absPath);   // read_file on this file is now allowed

            string[] lines     = await File.ReadAllLinesAsync(absPath, ct);
            long     sizeBytes = new FileInfo(absPath).Length;

            // Outline building lives in PreviewFormatter — one extractor shared with the client-disk path.
            return PreviewFormatter.Build(relPath, lines, sizeBytes);
        }
        catch (Exception ex)
        {
            return $"Error previewing file: {ex.Message}";
        }
    }

    // ── search_files (from SearchFiles.cs) ─────────────────────────────────────────────────────────
    public override async Task<string> Search(string argsJson)
    {
        if (brainVault) return BrainSearchRedirect;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string pattern    = doc.RootElement.GetProperty("pattern").GetString() ?? "";
            string relDir     = doc.RootElement.TryGetProperty("path",  out JsonElement pathEl) ? (pathEl.GetString() ?? ".").Trim('"', '\'', ' ') : ".";
            string glob       = doc.RootElement.TryGetProperty("glob",  out JsonElement globEl) ? globEl.GetString() ?? "*" : "*";
            bool   ignoreCase = doc.RootElement.TryGetProperty("ignore_case", out JsonElement icEl) && icEl.ValueKind == JsonValueKind.True;
            string? absDir = Resolve(relDir);
            if (absDir is null)
                return "Access denied: path traversal is not allowed.";
            if (!Directory.Exists(absDir))
                return $"Directory not found: {relDir}";

            Regex regex;
            try
            {
                RegexOptions opts = RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
                regex = new Regex(pattern, opts, TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException ex)
            {
                return $"Invalid regular expression: {ex.Message}";
            }

            List<string> results   = new();
            bool         truncated = false;
            int          totalChars = 0;
            foreach (string file in Directory.EnumerateFiles(absDir, glob, SearchOption.AllDirectories))
            {
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsIgnored(file)) continue;
                try
                {
                    string[] lines = await File.ReadAllLinesAsync(file, ct);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            string rel  = Path.GetRelativePath(root, file);
                            string line = $"{rel}:{i + 1}: {lines[i].Trim()}";
                            results.Add(line);
                            totalChars += line.Length + 1;
                            if (results.Count >= MAX_RESULTS || totalChars >= SEARCH_MAX_CHARS) { truncated = true; break; }
                        }
                    }
                }
                catch { /* skip unreadable / binary files */ }
                if (truncated) break;
            }
            if (results.Count == 0) return $"No matches found for /{pattern}/.";
            string tail = truncated ? $"\n... (truncated — narrow with path or glob to see more)" : "";
            return $"[search: /{pattern}/]\n{string.Join("\n", results)}{tail}";
        }
        catch (Exception ex) { return $"Error searching files: {ex.Message}"; }
    }

    /// <summary>True if any path segment below the project root is an ignored directory.</summary>
    private bool IsIgnored(string absFile)
    {
        string rel = Path.GetRelativePath(root, absFile);
        foreach (string seg in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (IgnoredDirs.Contains(seg)) return true;
        return false;
    }

    // ── find_files (from FindFiles.cs) ─────────────────────────────────────────────────────────────
    public override Task<string> Find(string argsJson)
    {
        if (brainVault) return Task.FromResult(BrainSearchRedirect);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string pattern = (doc.RootElement.GetProperty("pattern").GetString() ?? "").Trim();
            string relDir  = doc.RootElement.TryGetProperty("path", out JsonElement pe) ? (pe.GetString() ?? ".").Trim('"', '\'', ' ') : ".";
            if (pattern.Length == 0) return Task.FromResult("No pattern provided.");
            string? absDir = Resolve(relDir);
            if (absDir is null)            return Task.FromResult("Access denied: path traversal is not allowed.");
            if (!Directory.Exists(absDir)) return Task.FromResult($"Directory not found: {relDir}");

            Regex rx = GlobToRegex(pattern);
            List<string> results = new();
            bool truncated = false;
            foreach (string file in Directory.EnumerateFiles(absDir, "*", SearchOption.AllDirectories))
            {
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (rel.Split('/').Any(seg => IgnoredDirs.Contains(seg))) continue;
                if (rx.IsMatch(rel) || rx.IsMatch(Path.GetFileName(rel)))
                {
                    results.Add(rel);
                    if (results.Count >= MAX_RESULTS) { truncated = true; break; }
                }
            }
            if (results.Count == 0) return Task.FromResult($"No files found matching \"{pattern}\".");
            results.Sort(StringComparer.OrdinalIgnoreCase);
            string tail = truncated ? $"\n... (truncated at {MAX_RESULTS})" : "";
            return Task.FromResult($"[find: \"{pattern}\"]\n{string.Join("\n", results)}{tail}");
        }
        catch (Exception ex) { return Task.FromResult($"Error finding files: {ex.Message}"); }
    }

    /// <summary>Translates a glob (**, *, ?) to an anchored regex matched against a forward-slash path.</summary>
    private static Regex GlobToRegex(string glob)
    {
        StringBuilder sb = new("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '.' or '(' or ')' or '+' or '|' or '^' or '$' or '{' or '}' or '[' or ']' or '\\':
                    sb.Append('\\').Append(c); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    // ── edit_file (from EditFile.cs) ───────────────────────────────────────────────────────────────
    private readonly record struct EditSpec(string New, int StartLine, int EndLine, int InsertAfter);
    private readonly record struct Span(int Start, int Len, string Rep);

    private static int ReadEditLine(JsonElement parent, string name)
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

    public override async Task<string> Edit(string argsJson)
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

            static EditSpec Parse(JsonElement e) => new(
                Normalize(e.TryGetProperty("new_string", out var n) ? n.GetString() ?? "" : ""),
                ReadEditLine(e, "start_line"),
                ReadEditLine(e, "end_line"),
                ReadInsertAfter(e));

            List<EditSpec> edits = new();
            if (rootEl.TryGetProperty("edits", out JsonElement editsEl) && editsEl.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in editsEl.EnumerateArray()) edits.Add(Parse(e));
            else
                edits.Add(Parse(rootEl));
            if (edits.Count == 0)
                return $"No edits provided for {relPath}.";
            if (edits.Any(e => IsRedactionPlaceholder(e.New)))
                return $"Refused: a new_string was a placeholder (\"[omitted]\"), not real replacement text. That appears in the conversation only because an earlier payload was hidden to save space. Re-send the literal replacement text for {relPath}.";

            string content = await ReadWithRetry(absPath);
            string nl      = content.Contains("\r\n") ? "\r\n" : "\n";
            string buf0    = Normalize(content);

            List<int> lineStarts = new() { 0 };
            for (int i = 0; i < buf0.Length; i++) if (buf0[i] == '\n') lineStarts.Add(i + 1);
            int totalLines = lineStarts.Count;

            List<Span> spans = new();
            bool multi = edits.Count > 1;
            for (int i = 0; i < edits.Count; i++)
            {
                EditSpec ed = edits[i];
                string label = multi ? $" (edit {i + 1} of {edits.Count})" : "";

                if (ed.New.Trim().Length > 0 && BlockAlreadyPresent(buf0, ed.New, out int dupLine))
                    return $"Refused{label}: that block is already present in {relPath} (around line {dupLine}). "
                         + "You already added it earlier — applying it again would create a duplicate (e.g. a "
                         + "duplicate method, which won't compile). If you meant to CHANGE it, edit those existing "
                         + "lines with start_line/end_line; otherwise the file already has your change — move on.";

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
                    if (insOff == buf0.Length && buf0.Length > 0 && !buf0.EndsWith('\n') && insRep.Length > 0)
                        insRep = "\n" + insRep;
                    spans.Add(new Span(insOff, 0, insRep));
                    continue;
                }

                if (ed.StartLine <= 0)
                    return $"edit_file requires start_line and end_line{label} (to replace lines) or insert_after (to add new lines) — the 1-based line numbers shown by read_file/search_files. Re-read {relPath} if you don't have them.";

                int s = ed.StartLine, en = ed.EndLine > 0 ? ed.EndLine : ed.StartLine;
                if (s < 1 || s > totalLines || en < s || en > totalLines)
                    return $"start_line/end_line {s}-{en} is out of range{label} — {relPath} has {totalLines} lines. "
                         + $"You do NOT need to re-read; here are the file's current line numbers — edit against these:\n\n{NumberedView(buf0, s, totalLines)}";

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

            spans.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = 1; i < spans.Count; i++)
                if (spans[i].Start < spans[i - 1].Start + spans[i - 1].Len)
                    return $"Edits overlap in {relPath} — two edits target the same region. Combine them into one edit.";

            int firstStart = spans.Count > 0 ? spans[0].Start : 0;
            int firstRepLen = spans.Count > 0 ? spans[0].Rep.Length : 0;

            string buf = buf0;
            foreach (Span sp in spans.OrderByDescending(s => s.Start))
                buf = buf[..sp.Start] + sp.Rep + buf[(sp.Start + sp.Len)..];

            // No-op guard: a replacement identical to the existing text changes nothing, but silently
            // "succeeds" — the model then can't tell why the file didn't change and repeats the same edit.
            // Tell it explicitly, and point at the real move (delete lines to REMOVE content).
            if (buf == buf0)
                return $"No change: your new_string is identical to the current content at those lines in {relPath} — nothing was written. Re-typing the same text does nothing. To REMOVE content (e.g. to reduce a note's outbound links), target those lines with an EMPTY new_string to delete them; to change it, write genuinely different text.";

            int open0 = buf0.Count(c => c == '{'), close0 = buf0.Count(c => c == '}');
            int open1 = buf.Count(c => c == '{'),  close1 = buf.Count(c => c == '}');
            if (open0 == close0 && open1 != close1)
                return $"Refused: this edit would leave {relPath} with unbalanced braces ({open1} `{{` vs {close1} `}}`) — it was balanced before, so a brace was dropped or added and the file would not compile. Nothing was written. Check the braces in your new_string: add a COMPLETE block with matching `{{` and `}}` (use insert_after to add a whole method/class), and don't replace or omit an existing brace line.";

            gate?.TakeSnapshot(absPath, content);
            gate?.InvalidateReads(absPath);   // line numbers shifted — recorded read ranges are stale
            await WriteWithRetry(absPath, buf.Replace("\n", nl));

            string[] lines = buf.Split('\n');

            if (multi)
                return $"Successfully edited {relPath}. Applied {edits.Count} edits ({spans.Count} replacements). "
                     + $"File is now {lines.Length} lines — its line numbers have shifted; edit against these current numbers "
                     + $"(no need to re-read):\n\n{NumberedView(buf, 1, lines.Length)}";

            int    editLine     = buf[..firstStart].Count(c => c == '\n');
            int    newLineCount = firstRepLen == 0 ? 1 : buf.Substring(firstStart, firstRepLen).Count(c => c == '\n') + 1;
            int    from         = Math.Max(0, editLine - 5);
            int    to           = Math.Min(lines.Length - 1, editLine + newLineCount + 4);
            string snippet      = string.Join("\n", lines[from..(to + 1)].Select((l, i) => $"{from + i + 1,6}: {l}"));
            return $"Successfully edited {relPath}.\n\n[Updated context — lines {from + 1}–{to + 1}]\n```\n{snippet}\n```";
        }
        catch (Exception ex) { return $"Error editing file: {ex.Message}"; }
    }

    // Render 1-based numbered lines so a failed/batch edit can hand back CURRENT line numbers instead of
    // telling the model to re-read (which the memory-agent read guard blocks → blind re-read loop). Shows the
    // whole file when small; otherwise a window around the target line. Matches the read_file numbering format.
    private static string NumberedView(string content, int targetLine, int totalLines)
    {
        string[] lines = content.Replace("\r", "").Split('\n');
        int from, to;
        if (lines.Length <= 140) { from = 0; to = lines.Length - 1; }
        else { from = Math.Max(0, targetLine - 15); to = Math.Min(lines.Length - 1, targetLine + 15); }
        string body = string.Join("\n", lines[from..(to + 1)].Select((l, i) => $"{from + i + 1,6}: {l}"));
        string tag  = (from == 0 && to == lines.Length - 1) ? "whole file" : $"lines {from + 1}–{to + 1}";
        return $"[Current content — {tag}]\n```\n{body}\n```";
    }

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

    private static string LeadingWhitespace(string buf, int offset)
    {
        int i = offset;
        while (i < buf.Length && (buf[i] == ' ' || buf[i] == '\t')) i++;
        return buf[offset..i];
    }

    private static string MatchIndent(string rep, string targetIndent)
    {
        if (targetIndent.Length == 0 || rep.Length == 0 || rep[0] == '\n') return rep;

        int firstIndent = 0;
        while (firstIndent < rep.Length && (rep[firstIndent] == ' ' || rep[firstIndent] == '\t')) firstIndent++;
        if (firstIndent >= targetIndent.Length) return rep;

        return targetIndent[firstIndent..] + rep;
    }

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

    // ── delete_file (from DeleteFile.cs) ───────────────────────────────────────────────────────────
    public override Task<string> Delete(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string relPath = (doc.RootElement.GetProperty("path").GetString() ?? "").Trim('"', '\'', ' ');
            string? abs = Resolve(relPath);
            if (abs is null)         return Task.FromResult("Access denied: path traversal is not allowed.");
            if (!File.Exists(abs))   return Task.FromResult($"File not found: {relPath}");
            File.Delete(abs);
            return Task.FromResult($"Deleted {relPath}.");
        }
        catch (Exception ex) { return Task.FromResult($"Error deleting file: {ex.Message}"); }
    }

    // ── move_file (from MoveFile.cs) ───────────────────────────────────────────────────────────────
    public override Task<string> Move(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string src = (doc.RootElement.GetProperty("source").GetString()      ?? "").Trim('"', '\'', ' ');
            string dst = (doc.RootElement.GetProperty("destination").GetString() ?? "").Trim('"', '\'', ' ');
            string? absSrc = Resolve(src);
            string? absDst = Resolve(dst);
            if (absSrc is null || absDst is null) return Task.FromResult("Access denied: path traversal is not allowed.");
            if (!File.Exists(absSrc))             return Task.FromResult($"Source not found: {src}");
            if (File.Exists(absDst))              return Task.FromResult($"Destination already exists: {dst}. Choose a different name or delete it first.");
            string? dir = Path.GetDirectoryName(absDst);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.Move(absSrc, absDst);
            return Task.FromResult($"Moved {src} → {dst}.");
        }
        catch (Exception ex) { return Task.FromResult($"Error moving file: {ex.Message}"); }
    }

    // ── write_file (create a new file, or fully replace an existing one) ────────────────────────────
    public override async Task<string> Write(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            JsonElement root = doc.RootElement;
            string relPath = (root.GetProperty("path").GetString() ?? "").Trim('"', '\'', ' ');
            string content = root.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
            string? abs = Resolve(relPath);
            if (abs is null) return "Access denied: path traversal is not allowed.";
            if (IsRedactionPlaceholder(content))
                return $"Refused: content was a placeholder (\"[omitted]\"), not real text — re-send the literal content for {relPath}.";
            string? dir = Path.GetDirectoryName(abs);
            if (dir is not null) Directory.CreateDirectory(dir);
            bool existed = File.Exists(abs);
            // Snapshot the pre-write content so RevertFile can undo it (parity with Edit) — matters for the
            // coding pipeline; the memory agents revert via git instead.
            if (existed) gate?.TakeSnapshot(abs, await ReadWithRetry(abs));
            gate?.InvalidateReads(abs);   // content changed — recorded read ranges are stale
            await WriteWithRetry(abs, content);
            // "Successfully wrote" is the exact phrase Coder.ToolLoop keys on to register a write — keep it.
            return $"Successfully wrote {relPath} ({(existed ? "overwrote" : "created")}, {content.Split('\n').Length} lines).";
        }
        catch (Exception ex) { return $"Error writing file: {ex.Message}"; }
    }

    // ── list_directory (from ListDirectory.cs) ─────────────────────────────────────────────────────
    public override Task<string> List(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);

            string relPath = doc.RootElement.TryGetProperty("path", out JsonElement pathEl)
                ? (pathEl.GetString() ?? ".").Trim('"', '\'', ' ') : ".";

            // depth: int (1 = flat, N = recurse N levels). Also accept legacy recursive:true → depth=999.
            int depth = 1;
            if (doc.RootElement.TryGetProperty("depth", out JsonElement depthEl))
            {
                if (depthEl.ValueKind == JsonValueKind.Number) depth = depthEl.GetInt32();
                else if (depthEl.ValueKind == JsonValueKind.String && int.TryParse(depthEl.GetString(), out int dp)) depth = dp;
            }
            else if (doc.RootElement.TryGetProperty("recursive", out JsonElement recEl)
                && (recEl.ValueKind == JsonValueKind.True
                    || (recEl.ValueKind == JsonValueKind.String && bool.TryParse(recEl.GetString(), out bool rb) && rb)))
            {
                depth = 999;
            }

            string? absPath = Resolve(relPath);
            if (absPath is null)            return Task.FromResult("Access denied: path traversal is not allowed.");
            if (!Directory.Exists(absPath)) return Task.FromResult($"Directory not found: {relPath}");

            StringBuilder sb        = new();
            int           count     = 0;
            bool          truncated = false;

            sb.AppendLine($"[directory: \"{relPath}\"]");
            BuildTree(sb, absPath, "", 1, depth, ref count, ref truncated);

            if (truncated)
                sb.AppendLine($"... (truncated at {MAX_ENTRIES} entries — narrow with path or use search_files)");

            return Task.FromResult(sb.ToString().TrimEnd());
        }
        catch (Exception ex) { return Task.FromResult($"Error listing directory: {ex.Message}"); }
    }

    private void BuildTree(StringBuilder sb, string absDir, string indent, int currentDepth, int maxDepth, ref int count, ref bool truncated)
    {
        if (truncated) return;

        IOrderedEnumerable<string> entries = Directory.GetFileSystemEntries(absDir).OrderBy(Path.GetFileName);

        // Collect dirs and files separately so dirs are listed first
        List<string> dirs  = new();
        List<string> files = new();
        foreach (string entry in entries)
        {
            if (!entry.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(entry)) dirs.Add(entry);
            else files.Add(entry);
        }

        // At depths below maxDepth, show dirs with file count hint and recurse; show files too
        // At the deepest level (currentDepth == maxDepth) show everything flat
        foreach (string entry in dirs)
        {
            if (count >= MAX_ENTRIES) { truncated = true; return; }
            string name = Path.GetFileName(entry);
            if (currentDepth < maxDepth)
            {
                // Count direct source files for the hint
                int fc = 0;
                try { fc = Directory.GetFiles(entry).Length; } catch { /* ignore */ }
                string hint = fc > 0 ? $"  ({fc} file{(fc == 1 ? "" : "s")})" : "";
                sb.AppendLine($"{indent}{name}/{hint}");
                count++;
                BuildTree(sb, entry, indent + "  ", currentDepth + 1, maxDepth, ref count, ref truncated);
            }
            else
            {
                sb.AppendLine($"{indent}{name}/");
                count++;
            }
        }

        foreach (string entry in files)
        {
            if (count >= MAX_ENTRIES) { truncated = true; return; }
            sb.AppendLine($"{indent}{Path.GetFileName(entry)}");
            count++;
        }
    }

    // Preview outline building moved to PreviewFormatter (shared with the client-disk path).
}

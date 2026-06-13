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
            description = "Make a targeted find-and-replace edit to an existing file. old_string must match exactly once in the file — provide more surrounding context if needed to make it unique. Use write_file to create a new file or do a full rewrite.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path       = new { type = "string", description = "File path relative to project root" },
                    old_string = new { type = "string", description = "The exact text to find. Must appear exactly once in the file." },
                    new_string = new { type = "string", description = "The text to replace it with" }
                },
                required = new[] { "path", "old_string", "new_string" }
            }
        }
    };

    internal override async Task<string> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string relPath = (doc.RootElement.GetProperty("path").GetString()       ?? "").Trim('"', '\'', ' ');
            string oldStr  = doc.RootElement.GetProperty("old_string").GetString() ?? "";
            string newStr  = doc.RootElement.GetProperty("new_string").GetString() ?? "";
            string? absPath = Resolve(relPath);
            if (absPath is null)
                return "Access denied: path traversal is not allowed.";
            if (!File.Exists(absPath))
                return $"File not found: {relPath}";
            if (oldStr.Length == 0)
                return $"old_string is empty. Provide the exact text to replace in {relPath}.";

            string content = await File.ReadAllTextAsync(absPath, ct);

            // Match in normalized-LF space so CRLF/CR drift and indentation drift in the model's
            // old_string still resolve to the right region. Re-emit on the file's dominant ending.
            string nl          = content.Contains("\r\n") ? "\r\n" : "\n";
            string normContent = Normalize(content);
            string normOld     = Normalize(oldStr);
            string normNew     = Normalize(newStr);

            MatchResult match = FindMatch(normContent, normOld);

            if (match.Kind == MatchKind.Multiple)
                return $"old_string matches {match.Count} locations in {relPath}. Re-read the file and include more surrounding lines in old_string to make it unique.";

            if (match.Kind == MatchKind.None)
                return $"old_string not found in {relPath}. No changes made.{ClosestRegionHint(normContent, normOld)}";

            string normUpdated = normContent[..match.Start] + normNew + normContent[(match.Start + match.Length)..];
            await File.WriteAllTextAsync(absPath, normUpdated.Replace("\n", nl), ct);

            // Locate the edited region from the splice offset (counting preceding newlines), not by
            // searching for new_string's first line — that line may be blank or non-unique.
            int      editLine     = normUpdated[..match.Start].Count(c => c == '\n');
            string[] lines        = normUpdated.Split('\n');
            int      newLineCount = normNew.Length == 0 ? 1 : normNew.Count(c => c == '\n') + 1;
            int      from         = Math.Max(0, editLine - 5);
            int      to           = Math.Min(lines.Length - 1, editLine + newLineCount + 4);
            string   snippet      = string.Join("\n", lines[from..(to + 1)].Select((l, i) => $"{from + i + 1,6}: {l}"));
            string   note         = match.Kind == MatchKind.Whitespace
                ? " (matched ignoring indentation/line-ending differences)" : "";
            return $"Successfully edited {relPath}.{note}\n\n[Updated context — lines {from + 1}–{to + 1}]\n```\n{snippet}\n```";
        }
        catch (Exception ex) { return $"Error editing file: {ex.Message}"; }
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

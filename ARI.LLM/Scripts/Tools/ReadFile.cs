using System.Text;
using System.Text.Json;

namespace ARI.LLM;

internal sealed class ReadFile : FileTool
{
    private readonly FileSnapshots? gate;
    internal ReadFile(string root, CancellationToken ct, FileSnapshots? gate = null) : base(root, ct) { this.gate = gate; }

    internal override string Name => "read_file";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "read_file",
            description =
                "Read lines from a source file. " +
                "ALWAYS prefer a specific range over a whole-file read: use search_files first to locate the relevant lines, then read only that range with start_line and end_line. " +
                "Only omit start_line/end_line when you genuinely need the whole file (e.g. a short config). " +
                "Whole-file reads of large files are capped — you will miss content unless you target the right range.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path       = new { type = "string",  description = "Path to the file relative to the project root." },
                    start_line = new { type = "integer", description = "First line to return (1-indexed, inclusive). Use this whenever you know roughly where the content is." },
                    end_line   = new { type = "integer", description = "Last line to return (1-indexed, inclusive). Pair with start_line — read a window, not the whole file." }
                },
                required   = new[] { "path" }
            }
        }
    };

    // Models emit line numbers as quoted strings ("275") under the text tool protocol; GetInt32() throws
    // on a String element. Cast tolerantly — accept a number or a numeric string, error only if neither.
    static bool TryGetLineArg(JsonElement el, out int value)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt32(out value);
        if (el.ValueKind == JsonValueKind.String) return int.TryParse(el.GetString()?.Trim('"', '\'', ' '), out value);
        value = 0;
        return false;
    }

    internal override async Task<string> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc    = JsonDocument.Parse(argsJson);
            string             relPath = (doc.RootElement.GetProperty("path").GetString() ?? string.Empty).Trim('"', '\'', ' ');
            string?            absPath = Resolve(relPath);

            if (absPath is null)        return "Access denied: path traversal is not allowed.";
            if (!File.Exists(absPath))  return $"File not found: {relPath}";
            // Preview-before-read: keeps context lean by forcing the model to see the line count and the
            // large-file warning (and pick a range) before pulling content. preview_file marks the file.
            if (gate is not null && !gate.WasPreviewed(absPath))
                return $"[System: Call preview_file on {relPath} first — it shows the line count and warns if the file is large — then read_file only the line ranges you need. This keeps context lean.]";

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

            // Cap whole-file reads so a single read can't blow the context window.
            // Targeted reads (start_line/end_line supplied) are not capped — the caller chose the range.
            const int READ_MAX_LINES = 800;
            const int READ_MAX_CHARS = 48000;
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
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc     = JsonDocument.Parse(args);
            string             relPath = doc.RootElement.GetProperty("path").GetString() ?? string.Empty;
            string             safe    = Path.GetFileName(relPath).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

            string suffix = "";
            if (doc.RootElement.TryGetProperty("start_line", out JsonElement s) &&
                doc.RootElement.TryGetProperty("end_line",   out JsonElement e) &&
                TryGetLineArg(s, out int sl) && TryGetLineArg(e, out int el))
                suffix = $" ({sl}–{el})";

            return $"<div class=\"tool-use\">Reading {safe}{suffix}</div>\n";
        }
        catch { return "<div class=\"tool-use\">Reading file</div>\n"; }
    };
}

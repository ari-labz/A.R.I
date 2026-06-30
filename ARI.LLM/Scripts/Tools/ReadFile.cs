using System.Text.Json;

namespace ARI.LLM;

/// <summary>read_file tool — thin wrapper that delegates to the thread's <see cref="FileSystem"/>.</summary>
internal sealed class ReadFile : Tool
{
    private readonly FileSystem fs;
    internal ReadFile(FileSystem fs) => this.fs = fs;

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

    internal override Task<string> Execute(string argsJson) => fs.Read(argsJson);

    // Models emit line numbers as quoted strings under the text protocol; cast tolerantly for the display label.
    private static bool TryGetLineArg(JsonElement el, out int value)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt32(out value);
        if (el.ValueKind == JsonValueKind.String) return int.TryParse(el.GetString()?.Trim('"', '\'', ' '), out value);
        value = 0;
        return false;
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

using System.Text;
using System.Text.Json;

namespace ARI.LLM;

internal sealed class ReadFile : FileTool
{
    internal ReadFile(string root, CancellationToken ct) : base(root, ct) { }

    internal override string Name => "read_file";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "read_file",
            description = "Read the contents of a source file in the project. Use this when you need to examine a specific file before answering. " +
                          "Use start_line and end_line to read a specific range rather than the whole file.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path       = new { type = "string",  description = "Path to the file relative to the project root." },
                    start_line = new { type = "integer", description = "First line to return (1-indexed, inclusive). Defaults to 1." },
                    end_line   = new { type = "integer", description = "Last line to return (1-indexed, inclusive). Defaults to end of file." }
                },
                required   = new[] { "path" }
            }
        }
    };

    internal override async Task<string> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc    = JsonDocument.Parse(argsJson);
            string             relPath = (doc.RootElement.GetProperty("path").GetString() ?? string.Empty).Trim('"', '\'', ' ');
            string?            absPath = Resolve(relPath);

            if (absPath is null)        return "Access denied: path traversal is not allowed.";
            if (!File.Exists(absPath))  return $"File not found: {relPath}";

            string[] lines      = await File.ReadAllLinesAsync(absPath, ct);
            int      totalLines = lines.Length;

            int startLine = doc.RootElement.TryGetProperty("start_line", out JsonElement startEl) ? startEl.GetInt32() : 1;
            int endLine   = doc.RootElement.TryGetProperty("end_line",   out JsonElement endEl)   ? endEl.GetInt32()   : totalLines;

            startLine = Math.Max(1,         Math.Min(startLine, totalLines));
            endLine   = Math.Max(startLine, Math.Min(endLine,   totalLines));

            bool   fullFile = startLine == 1 && endLine == totalLines;
            string header   = fullFile
                ? $"[file: \"{relPath}\" ({totalLines} lines)]"
                : $"[file: \"{relPath}\" lines {startLine}-{endLine} of {totalLines}]";

            StringBuilder sb = new();
            sb.AppendLine(header);
            sb.AppendLine("```");
            for (int i = startLine - 1; i < endLine; i++)
                sb.AppendLine($"{i + 1,6}: {lines[i]}");
            sb.Append("```");
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
                doc.RootElement.TryGetProperty("end_line",   out JsonElement e))
                suffix = $" ({s.GetInt32()}–{e.GetInt32()})";

            return $"<div class=\"tool-use\">Reading {safe}{suffix}</div>\n";
        }
        catch { return "<div class=\"tool-use\">Reading file</div>\n"; }
    };
}

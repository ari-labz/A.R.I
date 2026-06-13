using System.Text.Json;

namespace ARI.LLM;

internal sealed class WriteFile : FileTool
{
    internal WriteFile(string root, CancellationToken ct) : base(root, ct) { }

    internal override string Name => "write_file";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "write_file",
            description = "Write or create a file. Overwrites the file if it already exists and creates any missing parent directories. Prefer edit_file for targeted changes to existing files.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path    = new { type = "string", description = "File path relative to project root" },
                    content = new { type = "string", description = "The full content to write to the file" }
                },
                required = new[] { "path", "content" }
            }
        }
    };

    internal override async Task<string> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string relPath = (doc.RootElement.GetProperty("path").GetString()    ?? "").Trim('"', '\'', ' ');
            string content = doc.RootElement.GetProperty("content").GetString() ?? "";
            string? absPath = Resolve(relPath);
            if (absPath is null)
                return "Access denied: path traversal is not allowed.";
            string? dir = Path.GetDirectoryName(absPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(absPath, content, ct);
            // Report the line count back as ground truth so the model works from what is now on
            // disk rather than rewriting the file again from memory.
            int lineCount = content.Length == 0 ? 0 : content.Count(c => c == '\n') + 1;
            return $"Successfully wrote {relPath} ({lineCount} lines). The file now contains exactly the content you provided — do not rewrite it unless making a further change.";
        }
        catch (Exception ex) { return $"Error writing file: {ex.Message}"; }
    }

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string p = doc.RootElement.GetProperty("path").GetString() ?? "";
            return $"<div class=\"tool-use\">Writing {p.Replace("&", "&amp;").Replace("<", "&lt;")}</div>\n";
        }
        catch { return "<div class=\"tool-use\">Writing file</div>\n"; }
    };
}

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
            description = "Create a NEW file, or deliberately replace an entire existing file's contents. Overwrites the whole file and creates missing parent directories. Do NOT use write_file to change an existing file — adding a method, editing lines, or fixing call sites is always edit_file. In particular, if edit_file feels stuck (line numbers shifted, an edit didn't seem to land), the fix is to re-read the file for fresh line numbers and use search_files to find exact call sites — NOT to fall back to write_file. Rewriting a whole existing file from memory reliably drops or duplicates code and is never the right escape hatch.",
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
            // Guard: history compaction renders earlier write/edit payloads as "[content omitted]" /
            // "[omitted]". A weak model can copy that placeholder back as the real content and erase the
            // file. Never write a redaction placeholder.
            if (IsRedactionPlaceholder(content))
                return $"Refused: the content was a placeholder (\"{content.Trim()}\"), not real file content. That text appears in the conversation only because an earlier payload was hidden to save space — it is not the file. Re-send the full, literal content you want written to {relPath}.";
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

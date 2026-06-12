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
            string relPath = doc.RootElement.GetProperty("path").GetString()       ?? "";
            string oldStr  = doc.RootElement.GetProperty("old_string").GetString() ?? "";
            string newStr  = doc.RootElement.GetProperty("new_string").GetString() ?? "";
            string? absPath = Resolve(relPath);
            if (absPath is null)
                return "Access denied: path traversal is not allowed.";
            if (!File.Exists(absPath))
                return $"File not found: {relPath}";
            string content = await File.ReadAllTextAsync(absPath, ct);
            int count = 0, idx = 0;
            while ((idx = content.IndexOf(oldStr, idx, StringComparison.Ordinal)) >= 0) { count++; idx += oldStr.Length; }
            if (count == 0) return $"old_string not found in {relPath}. No changes made.";
            if (count > 1)  return $"old_string matches {count} locations in {relPath}. Add more surrounding context to make it unique.";
            await File.WriteAllTextAsync(absPath, content.Replace(oldStr, newStr, StringComparison.Ordinal), ct);
            return $"Successfully edited {relPath}.";
        }
        catch (Exception ex) { return $"Error editing file: {ex.Message}"; }
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

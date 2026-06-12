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
            description = "Read the contents of a source file in the project. Use this when you need to examine a specific file before answering.",
            parameters  = new
            {
                type       = "object",
                properties = new { path = new { type = "string", description = "Path to the file relative to the project root" } },
                required   = new[] { "path" }
            }
        }
    };

    internal override async Task<string> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string relPath = doc.RootElement.GetProperty("path").GetString() ?? string.Empty;
            string? absPath = Resolve(relPath);
            if (absPath is null)
                return "Access denied: path traversal is not allowed.";
            if (!File.Exists(absPath))
                return $"File not found: {relPath}";
            string content = await File.ReadAllTextAsync(absPath, ct);
            return $"[file: \"{relPath}\"]\n```\n{content}\n```";
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
            using JsonDocument doc = JsonDocument.Parse(args);
            string relPath  = doc.RootElement.GetProperty("path").GetString() ?? string.Empty;
            string fileName = Path.GetFileName(relPath);
            string safe     = fileName.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            return $"<div class=\"tool-use\">Reading {safe}</div>\n";
        }
        catch { return "<div class=\"tool-use\">Reading file</div>\n"; }
    };
}

using System.Text.Json;

namespace ARI.LLM;

internal sealed class ListDirectory : FileTool
{
    internal ListDirectory(string root, CancellationToken ct) : base(root, ct) { }

    internal override string Name => "list_directory";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "list_directory",
            description = "List the files and subdirectories at a path within the project.",
            parameters  = new
            {
                type       = "object",
                properties = new { path = new { type = "string", description = "Directory path relative to project root. Defaults to project root if omitted." } },
                required   = Array.Empty<string>()
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string relPath = doc.RootElement.TryGetProperty("path", out JsonElement pathEl)
                ? pathEl.GetString() ?? "." : ".";
            string? absPath = Resolve(relPath);
            if (absPath is null)
                return Task.FromResult("Access denied: path traversal is not allowed.");
            if (!Directory.Exists(absPath))
                return Task.FromResult($"Directory not found: {relPath}");
            IEnumerable<string> entries = Directory.GetFileSystemEntries(absPath)
                .Select(e => Path.GetRelativePath(root, e) + (Directory.Exists(e) ? "/" : ""))
                .OrderBy(e => e);
            return Task.FromResult($"[directory: \"{relPath}\"]\n{string.Join("\n", entries)}");
        }
        catch (Exception ex) { return Task.FromResult($"Error listing directory: {ex.Message}"); }
    }

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string p = doc.RootElement.TryGetProperty("path", out JsonElement pe) ? pe.GetString() ?? "." : ".";
            return $"<div class=\"tool-use\">Listing {p.Replace("&", "&amp;").Replace("<", "&lt;")}</div>\n";
        }
        catch { return "<div class=\"tool-use\">Listing directory</div>\n"; }
    };
}

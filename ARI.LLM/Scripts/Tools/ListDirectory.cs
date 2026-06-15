using System.Text;
using System.Text.Json;

namespace ARI.LLM;

internal sealed class ListDirectory : FileTool
{
    private const int MAX_ENTRIES = 200;

    internal ListDirectory(string root, CancellationToken ct) : base(root, ct) { }

    internal override string Name => "list_directory";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "list_directory",
            description = $"List the files and subdirectories at a path within the project. " +
                          $"Set recursive to true to see the full tree (capped at {MAX_ENTRIES} entries).",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path      = new { type = "string",  description = "Directory path relative to project root. Defaults to project root if omitted." },
                    recursive = new { type = "boolean", description = $"If true, list all nested files and folders as a tree (max {MAX_ENTRIES} entries). Defaults to false." }
                },
                required = Array.Empty<string>()
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);

            string relPath  = doc.RootElement.TryGetProperty("path",      out JsonElement pathEl) ? (pathEl.GetString() ?? ".").Trim('"', '\'', ' ') : ".";
            bool   recurse  = doc.RootElement.TryGetProperty("recursive", out JsonElement recEl)  && recEl.GetBoolean();
            string? absPath = Resolve(relPath);

            if (absPath is null)             return Task.FromResult("Access denied: path traversal is not allowed.");
            if (!Directory.Exists(absPath))  return Task.FromResult($"Directory not found: {relPath}");

            if (!recurse)
            {
                IEnumerable<string> entries = Directory.GetFileSystemEntries(absPath)
                    .Select(e => Path.GetFileName(e) + (Directory.Exists(e) ? "/" : ""))
                    .OrderBy(e => e);
                return Task.FromResult($"[directory: \"{relPath}\"]\n{string.Join("\n", entries)}");
            }

            StringBuilder sb        = new();
            int           count     = 0;
            bool          truncated = false;

            sb.AppendLine($"[directory: \"{relPath}\" (recursive)]");
            BuildTree(sb, absPath, "", ref count, ref truncated);

            if (truncated)
                sb.AppendLine($"... (truncated at {MAX_ENTRIES} entries — narrow with path or use search_files)");

            return Task.FromResult(sb.ToString().TrimEnd());
        }
        catch (Exception ex) { return Task.FromResult($"Error listing directory: {ex.Message}"); }
    }

    private void BuildTree(StringBuilder sb, string absDir, string indent, ref int count, ref bool truncated)
    {
        if (truncated) return;

        IOrderedEnumerable<string> entries = Directory.GetFileSystemEntries(absDir).OrderBy(Path.GetFileName);
        foreach (string entry in entries)
        {
            if (count >= MAX_ENTRIES) { truncated = true; return; }
            if (!entry.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

            bool   isDir = Directory.Exists(entry);
            string name  = Path.GetFileName(entry) + (isDir ? "/" : "");
            sb.AppendLine($"{indent}{name}");
            count++;

            if (isDir)
                BuildTree(sb, entry, indent + "  ", ref count, ref truncated);
        }
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

using System.Text.Json;

namespace ARI.LLM;

internal sealed class SearchFiles : FileTool
{
    private const int MAX_RESULTS = 200;

    internal SearchFiles(string root, CancellationToken ct) : base(root, ct) { }

    internal override string Name => "search_files";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "search_files",
            description = "Search for a string across files in the project. Returns matching lines with file path and line number.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    pattern = new { type = "string", description = "Text to search for (case-insensitive)" },
                    path    = new { type = "string", description = "Directory to search in, relative to project root. Defaults to project root." },
                    glob    = new { type = "string", description = "File filter pattern e.g. '*.cs', '*.json'. Defaults to all files." }
                },
                required = new[] { "pattern" }
            }
        }
    };

    internal override async Task<string> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string pattern = doc.RootElement.GetProperty("pattern").GetString() ?? "";
            string relDir  = doc.RootElement.TryGetProperty("path", out JsonElement pathEl) ? pathEl.GetString() ?? "." : ".";
            string glob    = doc.RootElement.TryGetProperty("glob", out JsonElement globEl) ? globEl.GetString() ?? "*" : "*";
            string? absDir = Resolve(relDir);
            if (absDir is null)
                return "Access denied: path traversal is not allowed.";
            if (!Directory.Exists(absDir))
                return $"Directory not found: {relDir}";
            List<string> results = new();
            foreach (string file in Directory.EnumerateFiles(absDir, glob, SearchOption.AllDirectories))
            {
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    string[] lines = await File.ReadAllLinesAsync(file, ct);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            string rel = Path.GetRelativePath(root, file);
                            results.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
                        }
                    }
                }
                catch { /* skip unreadable files */ }
                if (results.Count >= MAX_RESULTS) break;
            }
            return results.Count == 0
                ? $"No matches found for \"{pattern}\"."
                : $"[search: \"{pattern}\"]\n{string.Join("\n", results)}";
        }
        catch (Exception ex) { return $"Error searching files: {ex.Message}"; }
    }

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string p = doc.RootElement.GetProperty("pattern").GetString() ?? "";
            return $"<div class=\"tool-use\">Searching for {p.Replace("&", "&amp;").Replace("<", "&lt;")}</div>\n";
        }
        catch { return "<div class=\"tool-use\">Searching files</div>\n"; }
    };
}

using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARI.LLM;

internal sealed class SearchFiles : FileTool
{
    private const int MAX_RESULTS  = 200;
    private const int MAX_CHARS    = 8000;

    // Directories that are never source code — skipped so searches stay fast and relevant.
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "dist", "build", ".next", ".nuxt",
        ".idea", ".vs", ".vscode", "coverage", "out", "target", "vendor", "packages",
        ".gradle", "Pods", "__pycache__", "Models", "Voices", "External"
    };

    internal SearchFiles(string root, CancellationToken ct) : base(root, ct) { }

    internal override string Name => "search_files";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "search_files",
            description = "Search file contents across the project with a regular expression (.NET regex). Returns matching lines with file path and line number. Case-sensitive by default; set ignore_case or use an inline (?i) flag. Build/VCS directories (node_modules, bin, obj, .git, …) are skipped.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    pattern     = new { type = "string",  description = "Regular expression to search for, e.g. 'public .* GrantAccess\\('." },
                    path        = new { type = "string",  description = "Directory to search in, relative to project root. Defaults to project root." },
                    glob        = new { type = "string",  description = "File filter pattern e.g. '*.cs', '*.json'. Defaults to all files." },
                    ignore_case = new { type = "boolean", description = "Case-insensitive match. Defaults to false." }
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
            string pattern    = doc.RootElement.GetProperty("pattern").GetString() ?? "";
            string relDir     = doc.RootElement.TryGetProperty("path",  out JsonElement pathEl) ? (pathEl.GetString() ?? ".").Trim('"', '\'', ' ') : ".";
            string glob       = doc.RootElement.TryGetProperty("glob",  out JsonElement globEl) ? globEl.GetString() ?? "*" : "*";
            bool   ignoreCase = doc.RootElement.TryGetProperty("ignore_case", out JsonElement icEl) && icEl.ValueKind == JsonValueKind.True;
            string? absDir = Resolve(relDir);
            if (absDir is null)
                return "Access denied: path traversal is not allowed.";
            if (!Directory.Exists(absDir))
                return $"Directory not found: {relDir}";

            Regex regex;
            try
            {
                RegexOptions opts = RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
                regex = new Regex(pattern, opts, TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException ex)
            {
                return $"Invalid regular expression: {ex.Message}";
            }

            List<string> results   = new();
            bool         truncated = false;
            int          totalChars = 0;
            foreach (string file in Directory.EnumerateFiles(absDir, glob, SearchOption.AllDirectories))
            {
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsIgnored(file)) continue;
                try
                {
                    string[] lines = await File.ReadAllLinesAsync(file, ct);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            string rel  = Path.GetRelativePath(root, file);
                            string line = $"{rel}:{i + 1}: {lines[i].Trim()}";
                            results.Add(line);
                            totalChars += line.Length + 1;
                            if (results.Count >= MAX_RESULTS || totalChars >= MAX_CHARS) { truncated = true; break; }
                        }
                    }
                }
                catch { /* skip unreadable / binary files */ }
                if (truncated) break;
            }
            if (results.Count == 0) return $"No matches found for /{pattern}/.";
            string tail = truncated ? $"\n... (truncated — narrow with path or glob to see more)" : "";
            return $"[search: /{pattern}/]\n{string.Join("\n", results)}{tail}";
        }
        catch (Exception ex) { return $"Error searching files: {ex.Message}"; }
    }

    /// <summary>True if any path segment below the project root is an ignored directory.</summary>
    private bool IsIgnored(string absFile)
    {
        string rel = Path.GetRelativePath(root, absFile);
        foreach (string seg in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (IgnoredDirs.Contains(seg)) return true;
        return false;
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

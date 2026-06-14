using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>Finds files by name using a glob pattern. Complements search_files (which matches contents).</summary>
internal sealed class FindFiles : FileTool
{
    private const int MAX_RESULTS = 200;

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "dist", "build", ".next", ".nuxt",
        ".idea", ".vs", ".vscode", "coverage", "out", "target", "vendor", "packages",
        ".gradle", "Pods", "__pycache__", "Models", "Voices", "External"
    };

    internal FindFiles(string root, CancellationToken ct) : base(root, ct) { }

    internal override string Name => "find_files";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "find_files",
            description = "Find files by name with a glob pattern, e.g. '*.cs', 'Token*.cs', or '**/Security/*.cs'. Returns paths relative to the project root. Build/VCS directories are skipped. Use search_files to match file contents.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    pattern = new { type = "string", description = "Glob pattern, e.g. '*.cs' or '**/Token*.cs'." },
                    path    = new { type = "string", description = "Directory to search under, relative to project root. Defaults to root." }
                },
                required = new[] { "pattern" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string pattern = (doc.RootElement.GetProperty("pattern").GetString() ?? "").Trim();
            string relDir  = doc.RootElement.TryGetProperty("path", out JsonElement pe) ? (pe.GetString() ?? ".").Trim('"', '\'', ' ') : ".";
            if (pattern.Length == 0) return Task.FromResult("No pattern provided.");
            string? absDir = Resolve(relDir);
            if (absDir is null)            return Task.FromResult("Access denied: path traversal is not allowed.");
            if (!Directory.Exists(absDir)) return Task.FromResult($"Directory not found: {relDir}");

            Regex rx = GlobToRegex(pattern);
            List<string> results = new();
            bool truncated = false;
            foreach (string file in Directory.EnumerateFiles(absDir, "*", SearchOption.AllDirectories))
            {
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (rel.Split('/').Any(seg => IgnoredDirs.Contains(seg))) continue;
                if (rx.IsMatch(rel) || rx.IsMatch(Path.GetFileName(rel)))
                {
                    results.Add(rel);
                    if (results.Count >= MAX_RESULTS) { truncated = true; break; }
                }
            }
            if (results.Count == 0) return Task.FromResult($"No files found matching \"{pattern}\".");
            results.Sort(StringComparer.OrdinalIgnoreCase);
            string tail = truncated ? $"\n... (truncated at {MAX_RESULTS})" : "";
            return Task.FromResult($"[find: \"{pattern}\"]\n{string.Join("\n", results)}{tail}");
        }
        catch (Exception ex) { return Task.FromResult($"Error finding files: {ex.Message}"); }
    }

    /// <summary>Translates a glob (**, *, ?) to an anchored regex matched against a forward-slash path.</summary>
    private static Regex GlobToRegex(string glob)
    {
        StringBuilder sb = new("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '.' or '(' or ')' or '+' or '|' or '^' or '$' or '{' or '}' or '[' or ']' or '\\':
                    sb.Append('\\').Append(c); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string p = doc.RootElement.GetProperty("pattern").GetString() ?? "";
            return $"<div class=\"tool-use\">Finding {p.Replace("&", "&amp;").Replace("<", "&lt;")}</div>\n";
        }
        catch { return "<div class=\"tool-use\">Finding files</div>\n"; }
    };
}

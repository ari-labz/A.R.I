using System.Text.Json;

namespace ARI.LLM;

/// <summary>search_files tool — thin wrapper that delegates to the thread's <see cref="FileSystem"/>.</summary>
internal sealed class SearchFiles : Tool
{
    private readonly FileSystem fs;
    internal SearchFiles(FileSystem fs) => this.fs = fs;

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
                    pattern     = new { type = "string",  description = "Regular expression to search for, e.g. 'public .* MethodName\\('." },
                    path        = new { type = "string",  description = "Directory to search in, relative to project root. Defaults to project root." },
                    glob        = new { type = "string",  description = "File filter pattern e.g. '*.cs', '*.json'. Defaults to all files." },
                    ignore_case = new { type = "boolean", description = "Case-insensitive match. Defaults to false." }
                },
                required = new[] { "pattern" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson) => fs.Search(argsJson);

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string p = doc.RootElement.GetProperty("pattern").GetString() ?? "";
            return $"<!--ari-tool-start:search_files:{p.Replace("&", "&amp;").Replace("<", "&lt;").Replace("--", "&#45;&#45;")}-->";
        }
        catch { return "<!--ari-tool-start:search_files:files-->"; }
    };
}

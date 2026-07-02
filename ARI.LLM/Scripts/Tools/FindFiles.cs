using System.Text.Json;

namespace ARI.LLM;

/// <summary>find_files tool — thin wrapper that delegates to the thread's <see cref="FileSystem"/>.</summary>
internal sealed class FindFiles : Tool
{
    private readonly FileSystem fs;
    internal FindFiles(FileSystem fs) => this.fs = fs;

    internal override string Name => "find_files";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "find_files",
            description = "Find files by name with a glob pattern, e.g. '*.cs', 'User*.cs', or '**/Services/*.cs'. Returns paths relative to the project root. Build/VCS directories are skipped. Use search_files to match file contents.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    pattern = new { type = "string", description = "Glob pattern, e.g. '*.cs' or '**/User*.cs'." },
                    path    = new { type = "string", description = "Directory to search under, relative to project root. Defaults to root." }
                },
                required = new[] { "pattern" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson) => fs.Find(argsJson);

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string p = doc.RootElement.GetProperty("pattern").GetString() ?? "";
            return $"<!--ari-tool-start:find_files:{p.Replace("&", "&amp;").Replace("<", "&lt;").Replace("--", "&#45;&#45;")}-->";
        }
        catch { return "<!--ari-tool-start:find_files:files-->"; }
    };
}

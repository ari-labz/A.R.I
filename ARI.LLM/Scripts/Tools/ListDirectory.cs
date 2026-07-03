using System.Text.Json;

namespace ARI.LLM;

/// <summary>list_directory tool — thin wrapper that delegates to the thread's <see cref="FileSystem"/>.</summary>
internal sealed class ListDirectory : Tool
{
    private const int MAX_ENTRIES = 200;

    private readonly FileSystem fs;
    internal ListDirectory(FileSystem fs) => this.fs = fs;

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

    internal override Task<string> Execute(string argsJson) => fs.List(argsJson);

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string p = doc.RootElement.TryGetProperty("path", out JsonElement pe) ? pe.GetString() ?? "." : ".";
            return $"<!--ari-tool-start:list_directory:{p.Replace("&", "&amp;").Replace("<", "&lt;").Replace("--", "&#45;&#45;")}-->";
        }
        catch { return "<!--ari-tool-start:list_directory:directory-->"; }
    };
}

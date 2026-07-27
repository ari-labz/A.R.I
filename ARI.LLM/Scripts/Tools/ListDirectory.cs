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
            description = "List the contents of a directory. By default shows files and immediate subdirectories " +
                          "(depth=1). Set depth to 2 or more to see nested folders. Directories include a file count " +
                          $"hint so you know which ones are worth exploring. Capped at {MAX_ENTRIES} total entries.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path  = new { type = "string",  description = "Directory path relative to project root. Omit for project root." },
                    depth = new { type = "integer",  description = "How many levels to recurse. 1 = immediate contents only (default), 2+ = nested. Files are only shown at the deepest level." }
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

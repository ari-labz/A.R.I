using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// search_vault — regex search over a project's ObsidianGraph notes. A thin, note-flavored adapter
/// over the same search FileSystem.Search already does for search_files (glob forced to *.md, always
/// case-insensitive since prose isn't code-cased) — no index, no database, nothing shared with the
/// brain vault (ARI.Brain). Zero-config for the model: one 'query' field instead of pattern/path/glob.
/// </summary>
internal sealed class SearchVault : Tool
{
    private readonly FileSystem fs;
    internal SearchVault(FileSystem fs) => this.fs = fs;

    internal override string Name => "search_vault";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "search_vault",
            description = "Search this project's notes with a regular expression (.NET regex), matched against note content. Case-insensitive. Returns matching lines with note path and line number.",
            parameters  = new
            {
                type       = "object",
                properties = new { query = new { type = "string", description = "Regular expression to search for, e.g. 'chapter \\d+' or a plain word/phrase." } },
                required   = new[] { "query" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        string query;
        try { query = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.GetProperty("query").GetString() ?? ""; }
        catch { query = ""; }
        if (query.Length == 0) return Task.FromResult("Error: 'query' is required.");

        string forwardedArgs = JsonSerializer.Serialize(new { pattern = query, glob = "*.md", ignore_case = true });
        return fs.Search(forwardedArgs);
    }

    internal override Func<string, string>? Display => _ => "<!--ari-tool-start:search_vault:vault-->";
}

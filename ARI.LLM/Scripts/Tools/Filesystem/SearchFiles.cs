using System.Text.Json;

namespace ARI.LLM;

/// <summary>search_files tool — thin wrapper that delegates to the thread's <see cref="FileSystem"/>.</summary>
internal sealed class SearchFiles : Tool
{
    private readonly FileSystem fs;
    internal SearchFiles(FileSystem fs) => this.fs = fs;

    internal override string     Name   => "search_files";
    internal override ToolAccess Access => ToolAccess.Read;

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

    private const int MAX_RESULTS = 50;

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        string result = await fs.Search(argsJson);
        return Cap(result);
    }

    private static string Cap(string result)
    {
        string[] lines = result.Split('\n');
        int count = 0;
        foreach (string line in lines)
            if (!string.IsNullOrWhiteSpace(line))
                count++;
        if (count <= MAX_RESULTS)
            return result;
        int kept = 0;
        List<string> keptLines = new();
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                keptLines.Add(line);
                continue;
            }
            if (kept >= MAX_RESULTS)
                break;
            keptLines.Add(line);
            kept++;
        }
        int hidden = count - MAX_RESULTS;
        keptLines.Add($"[truncated — {hidden} more results hidden]");
        return string.Join('\n', keptLines);
    }

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

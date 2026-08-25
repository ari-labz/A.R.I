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

    private const int MAX_RESULTS = 50;

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        string result = await fs.Find(argsJson);
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
            return $"<!--ari-tool-start:find_files:{p.Replace("&", "&amp;").Replace("<", "&lt;").Replace("--", "&#45;&#45;")}-->";
        }
        catch { return "<!--ari-tool-start:find_files:files-->"; }
    };
}

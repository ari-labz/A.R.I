using System.Text;

namespace ARI.LLM;

/// <summary>tool_search: find tools by what they do and load them in the same call. The way to discover a tool without
/// reading the whole list; list_tools stays as the overview. Matches the words of the query against each tool's name, group
/// and description; "select:a,b" loads exactly those.</summary>
internal sealed class ToolSearch : Tool
{
    private readonly Thread thread;
    internal ToolSearch(Thread thread) => this.thread = thread;

    internal override string     Name   => "tool_search";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "tool_search",
            description = "Find tools by what you want to do and load them so you can call them. Describe the job in a few words, " +
                          "e.g. \"read a pdf\", \"run python\", \"send an http request\", \"screenshot a page\". " +
                          "Use \"select:name1,name2\" to load tools whose names you already know. Do this before telling the user you can't do something.",
            parameters  = new
            {
                type       = "object",
                properties = new { query = new { type = "string", description = "What you want to do, or select:name1,name2." } },
                required   = new[] { "query" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        string query = "";
        try { query = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.GetProperty("query").GetString() ?? ""; } catch { }
        query = query.Trim();
        if (query.Length == 0) return Task.FromResult<ToolResult>("Error: say what you want to do.");

        List<string> names;
        if (query.StartsWith("select:", StringComparison.OrdinalIgnoreCase))
            names = query[7..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        else
        {
            string[] words = query.ToLowerInvariant().Split(new[] { ' ', ',', '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToArray();
            names = ToolFactories.AllNames()
                .Select(n => (Name: n, Score: Score(n, words)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Name).Take(12).ToList();
        }

        StringBuilder sb = new();
        int loaded = 0;
        foreach (string name in names)
        {
            if (thread.tools.ContainsKey(name)) { sb.AppendLine($"  • {name} — already loaded"); loaded++; continue; }
            if (!ToolFactories.TryBuild(name, thread, out Tool tool)) continue;
            tool.Register(thread);
            sb.AppendLine($"  • {tool.Name} — {tool.SchemaDescription}");
            if (++loaded >= 6) break;
        }
        return Task.FromResult<ToolResult>(loaded == 0
            ? $"No tool matched \"{query}\". Try different words, or call list_tools to see everything."
            : $"Loaded:\n{sb.ToString().TrimEnd()}");
    }

    private static int Score(string name, string[] words)
    {
        string group = ToolRegistry.GroupOf(name).ToLowerInvariant(), desc = ToolRegistry.DescriptionFor(name).ToLowerInvariant(), n = name.ToLowerInvariant();
        int score = 0;
        foreach (string w in words)
        {
            if (n.Contains(w)) score += 4;
            if (group.Contains(w)) score += 2;
            if (desc.Contains(w)) score += 1;
        }
        return score;
    }
}

namespace ARI.LLM;

/// <summary>Lists every deferred tool group AND every ungrouped tool, each with its one-line
/// description (issue #126). Registered on every thread — see LLMModule.GetOrCreateThread — so any
/// agent can discover everything request_tools can load, without those tools' full schemas sitting in
/// context until actually requested. Grouping is purely a convenience for requesting several tools at
/// once — nothing here is hidden just because it isn't in a group.</summary>
internal sealed class ListTools : Tool
{
    private readonly Thread thread;
    internal ListTools(Thread thread) => this.thread = thread;

    internal override string     Name   => "list_tools";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "list_tools",
            description = "List every available tool group and every standalone (ungrouped) tool, with what each is for. Call this before request_tools if you don't already know the name you need.",
            parameters  = new { type = "object", properties = new { } }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        HashSet<string> grouped = ToolGroups.AllGroupedToolNames();
        List<string> ungroupedLines = new();
        foreach (string name in ToolFactories.AllNames().Where(n => !grouped.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            string description = ToolFactories.TryBuild(name, thread, out Tool tool) ? tool.SchemaDescription : string.Empty;
            ungroupedLines.Add(description.Length > 0 ? $"- {name}: {description}" : $"- {name}");
        }

        string groupsText = ToolGroups.ManifestText();
        if (ungroupedLines.Count == 0) return Task.FromResult<ToolResult>(groupsText);

        return Task.FromResult<ToolResult>(
            $"{groupsText}\n\nUngrouped tools (request by name, same as a group):\n{string.Join("\n", ungroupedLines)}");
    }
}

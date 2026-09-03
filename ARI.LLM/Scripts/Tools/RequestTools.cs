using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// Loads a deferred tool group, OR a single standalone tool, onto the calling thread (issue #126).
/// Generic and agent-agnostic: any thread can ask for any group or tool name. Whether it actually gets
/// tools back depends only on ToolFactories — which needs context (Thread.FilesystemRoot etc.) that may
/// or may not be bound on this thread — never on which agent is asking. Grouping only exists so several
/// tools can be pulled in one call; nothing is reachable ONLY through a group. See ToolFactories.cs for
/// the construction logic.
/// </summary>
internal sealed class RequestTools : Tool
{
    private readonly Thread thread;
    internal RequestTools(Thread thread) => this.thread = thread;

    internal override string Name => "request_tools";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "request_tools",
            description = "Load a tool group OR a single standalone tool by name so it becomes callable. Call list_tools first if you don't already know the name you need.",
            parameters  = new
            {
                type       = "object",
                properties = new { name = new { type = "string", description = "A tool group name (e.g. 'git_tools') or a single tool's own name (e.g. 'get_time')." } },
                required   = new[] { "name" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        string name;
        try { name = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.GetProperty("name").GetString() ?? ""; }
        catch { name = ""; }

        if (name.Length == 0) return Task.FromResult<ToolResult>("Error: 'name' is required.");

        if (ToolGroups.TryGet(name, out ToolGroupDef groupDef))
        {
            (List<Tool> loaded, List<string> unavailable) = ToolFactories.LoadGroup(name, thread);
            if (loaded.Count == 0) return Task.FromResult<ToolResult>($"'{name}' isn't available in this context (no project/vault is bound here).");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Group: {name} — {groupDef.Description}");
            sb.AppendLine("Tools loaded:");
            foreach (Tool tool in loaded)
                sb.AppendLine($"  • {tool.Name} — {tool.SchemaDescription}");
            if (unavailable.Count > 0)
                sb.AppendLine($"Not available here: {string.Join(", ", unavailable)}.");
            return Task.FromResult<ToolResult>(sb.ToString().TrimEnd());
        }

        if (ToolFactories.TryBuild(name, thread, out Tool single))
        {
            single.Register(thread);
            return Task.FromResult<ToolResult>($"Tool loaded: {single.Name} — {single.SchemaDescription}");
        }

        return Task.FromResult<ToolResult>(
            ToolFactories.AllNames().Contains(name, StringComparer.OrdinalIgnoreCase)
                ? $"'{name}' isn't available in this context (no project/vault is bound here)."
                : $"Unknown tool or group '{name}'. Call list_tools to see what's available.");
    }
}

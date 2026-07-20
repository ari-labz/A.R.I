using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// Loads a deferred tool group onto the calling thread (issue #126). A group's tool NAMES live in the
/// context-free ToolGroups catalog, but constructing the actual Tool instances (e.g. GitStatus needs a
/// root path) requires context only the owning agent has — so each agent that wants request_tools
/// supplies its own name→factory map at registration time. See MemoryAgent.RegisterTools for the first
/// real user (git_tools).
/// </summary>
internal sealed class RequestTools : Tool
{
    private readonly Thread thread;
    private readonly IReadOnlyDictionary<string, Func<Tool>> factories;

    internal RequestTools(Thread thread, IReadOnlyDictionary<string, Func<Tool>> factories)
    {
        this.thread    = thread;
        this.factories = factories;
    }

    internal override string Name => "request_tools";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "request_tools",
            description = "Load a tool group by name so its tools become callable. Call list_tools first if you don't already know the group name.",
            parameters  = new
            {
                type       = "object",
                properties = new { group = new { type = "string", description = "The tool group name, e.g. 'git_tools'." } },
                required   = new[] { "group" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        string group;
        try { group = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.GetProperty("group").GetString() ?? ""; }
        catch { group = ""; }

        if (group.Length == 0) return Task.FromResult("Error: 'group' is required.");
        if (!ToolGroups.TryGet(group, out ToolGroupDef def))
            return Task.FromResult($"Unknown tool group '{group}'. Call list_tools to see what's available.");

        List<string> loaded = new(), unavailable = new();
        foreach (string toolName in def.Tools)
        {
            if (factories.TryGetValue(toolName, out Func<Tool>? factory)) { factory().Register(thread); loaded.Add(toolName); }
            else unavailable.Add(toolName);
        }

        if (loaded.Count == 0) return Task.FromResult($"'{group}' isn't available in this context.");
        string result = $"Loaded: {string.Join(", ", loaded)}. They're ready to call now.";
        if (unavailable.Count > 0) result += $" (Not available here: {string.Join(", ", unavailable)}.)";
        return Task.FromResult(result);
    }
}

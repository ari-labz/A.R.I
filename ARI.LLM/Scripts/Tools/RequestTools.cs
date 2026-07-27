using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// Loads a deferred tool group onto the calling thread (issue #126). Generic and agent-agnostic: any
/// thread can ask for any group. Whether it actually gets tools back depends only on ToolFactories —
/// which needs context (Thread.ProjectRoot etc.) that may or may not be bound on this thread — never on
/// which agent is asking. See ToolFactories.cs for the construction logic.
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
        if (!ToolGroups.TryGet(group, out _))
            return Task.FromResult($"Unknown tool group '{group}'. Call list_tools to see what's available.");

        (List<string> loaded, List<string> unavailable) = ToolFactories.LoadGroup(group, thread);

        if (loaded.Count == 0) return Task.FromResult($"'{group}' isn't available in this context (no project/vault is bound here).");
        string result = $"Loaded: {string.Join(", ", loaded)}. They're ready to call now.";
        if (unavailable.Count > 0) result += $" (Not available here: {string.Join(", ", unavailable)}.)";
        return Task.FromResult(result);
    }
}

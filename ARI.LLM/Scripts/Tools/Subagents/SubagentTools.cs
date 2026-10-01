using System.Text.Json;

namespace ARI.LLM;

/// <summary>spawn_agent: start a subagent with a title, a task prompt and context. Returns at once with its id; see
/// <see cref="SubagentManager"/>.</summary>
internal sealed class SpawnAgent(Thread parent) : Tool
{
    internal override string Name => "spawn_agent";

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "spawn_agent",
            description = "Start a subagent on one task in the background; returns its id at once. It starts with a blank memory, so give it the context it needs. It can use a subset of the tools already loaded here (default: read-only file, web and memory lookups). Call wait_for_agent when you need the result.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    title   = new { type = "string", description = "A short name for the task, e.g. \"Python version\"." },
                    prompt  = new { type = "string", description = "What to do and what to report back." },
                    context = new { type = "string", description = "Everything it needs to know from this conversation: relevant facts, who's asking, constraints. It can't see this conversation." },
                    tools   = new { type = "array", items = new { type = "string" }, description = "Tool names to give it, from those loaded here. Omit for read-only lookups." }
                },
                required = new[] { "title", "prompt" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        string title = "", task = "";
        string? context = null;
        List<string>? tools = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            title   = doc.RootElement.TryGetProperty("title",   out JsonElement ti) ? ti.GetString() ?? "" : "";
            task    = doc.RootElement.TryGetProperty("prompt",  out JsonElement pr) ? pr.GetString() ?? "" : "";
            context = doc.RootElement.TryGetProperty("context", out JsonElement cx) ? cx.GetString() : null;
            if (doc.RootElement.TryGetProperty("tools", out JsonElement t) && t.ValueKind == JsonValueKind.Array)
                tools = t.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
        }
        catch (JsonException) { return Task.FromResult<ToolResult>("[Error: arguments weren't valid JSON]"); }

        (SubagentManager.Run? run, string message) = SubagentManager.Spawn(parent, title, task, context, tools);
        return Task.FromResult<ToolResult>(run is null ? $"[Error: {message}]" : message);
    }

    internal override Func<string, string>? Display => args =>
        $"<!--ari-tool-start:spawn_agent:{SubagentLabels.Title(args)}-->";
}

/// <summary>wait_for_agent: the holding pattern. Keeps this turn open, without generating, until the agents report.</summary>
internal sealed class WaitForAgent(Thread parent) : Tool
{
    internal override string Name => "wait_for_agent";

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "wait_for_agent",
            description = "Hold this reply open, without generating, until your agents finish; returns their results. Also returns early if the user sends a message or the wait times out, saying which agents are still running.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    ids         = new { type = "array", items = new { type = "integer" }, description = "Agent ids to wait for. Omit to wait for every agent whose result you haven't had yet." },
                    max_seconds = new { type = "integer", description = $"Longest to wait (default {SubagentManager.DefaultWaitSeconds}, max {SubagentManager.MaxWaitSeconds})." }
                }
            }
        }
    };

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        List<int>? ids = null;
        int maxSeconds = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("ids", out JsonElement i) && i.ValueKind == JsonValueKind.Array)
                ids = i.EnumerateArray().Where(e => e.TryGetInt32(out _)).Select(e => e.GetInt32()).ToList();
            if (doc.RootElement.TryGetProperty("max_seconds", out JsonElement m) && m.TryGetInt32(out int s))
                maxSeconds = s;
        }
        catch (JsonException) { }
        return await SubagentManager.Wait(parent, ids, maxSeconds, parent.Ct);
    }

    internal override Func<string, string>? Display => args =>
        $"<!--ari-tool-start:wait_for_agent:{SubagentLabels.Titles(parent, args)}-->";
}

/// <summary>cancel_agent: stop a running subagent.</summary>
internal sealed class CancelAgent(Thread parent) : Tool
{
    internal override string Name => "cancel_agent";

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "cancel_agent",
            description = "Stop a running subagent.",
            parameters  = new
            {
                type       = "object",
                properties = new { id = new { type = "integer", description = "The agent's id." } },
                required   = new[] { "id" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("id", out JsonElement i) && i.TryGetInt32(out int id))
                return Task.FromResult<ToolResult>(SubagentManager.Cancel(parent, id));
        }
        catch (JsonException) { }
        return Task.FromResult<ToolResult>("[Error: give the agent's id]");
    }
}

/// <summary>Chip labels for the subagent tools, safe for the tool-marker grammar.</summary>
internal static class SubagentLabels
{
    internal static string Title(string argsJson)
    {
        string title = ToolCallParser.TryExtractJsonString(argsJson, "title") ?? "";
        title = title.Replace("\n", " ").Replace("\r", " ").Replace(":", " ").Trim();
        if (title.Length > 60) title = title[..57].TrimEnd() + "…";
        return ToolCallParser.EscapeLabel(title.Length > 0 ? title : "agent");
    }

    /// <summary>The titles of the agents a wait covers, as "Go version, Python version|n=2".</summary>
    internal static string Titles(Thread parent, string argsJson)
    {
        List<int>? ids = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("ids", out JsonElement i) && i.ValueKind == JsonValueKind.Array)
                ids = i.EnumerateArray().Where(e => e.TryGetInt32(out _)).Select(e => e.GetInt32()).ToList();
        }
        catch (JsonException) { }
        List<string> titles = SubagentManager.Titles(parent, ids)
            .Select(t => t.Replace("\n", " ").Replace("\r", " ").Replace(":", " ").Replace("|", " ").Trim()).ToList();
        string joined = titles.Count > 0 ? string.Join(", ", titles) : "agents";
        if (joined.Length > 80) joined = joined[..77].TrimEnd() + "…";
        return ToolCallParser.EscapeLabel(joined) + $"|n={Math.Max(titles.Count, 1)}";
    }
}

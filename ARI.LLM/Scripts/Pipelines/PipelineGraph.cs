using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>One pipeline as the control panel's node editor draws it: START (the conversation) → agents → END (the user).
/// An agent's input ports are fixed by what it is — the built-ins name theirs in <see cref="PipelineGraph.BuiltInPorts"/>,
/// a custom agent gets one per {token} in its prompt. Each port takes exactly one wire.</summary>
internal sealed class PipelineGraph
{
    internal sealed record Node(string Id, string Type, string? Agent);
    internal sealed record Edge(string From, string To, string Port, bool Stream);

    internal string       Id    { get; init; } = "";
    internal string       Name  { get; init; } = "";
    internal List<Node>   Nodes { get; init; } = new();
    internal List<Edge>   Edges { get; init; } = new();

    internal const string StartId = "start", EndId = "end";

    /// <summary>The built-in agents that can sit in a graph, and the input ports each one has. "history" is the START output.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> BuiltInPorts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Memory"]   = ["history"],
        ["Dialogue"] = ["history", "memories"],
    };

    /// <summary>Tokens a custom agent's prompt can use without wiring: the user's latest message.</summary>
    internal static readonly string[] ImplicitTokens = ["message"];

    private static readonly Regex TokenRx = new(@"\{(\w+)\}", RegexOptions.Compiled);

    internal static IReadOnlyList<string> PromptTokens(string prompt) =>
        TokenRx.Matches(prompt).Select(m => m.Groups[1].Value)
            .Where(t => !ImplicitTokens.Contains(t, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();

    internal static string Fill(string prompt, IReadOnlyDictionary<string, string> values) =>
        TokenRx.Replace(prompt, m => values.TryGetValue(m.Groups[1].Value, out string? v) ? v : m.Value);

    internal static List<PipelineGraph> ParseAll(JsonElement root)
    {
        List<PipelineGraph> result = new();
        if (!root.TryGetProperty("pipelines", out JsonElement arr) && !root.TryGetProperty("Pipelines", out arr)) return result;
        foreach (JsonElement p in arr.EnumerateArray())
        {
            List<Node> nodes = new(); List<Edge> edges = new();
            if (p.TryGetProperty("nodes", out JsonElement ns))
                foreach (JsonElement n in ns.EnumerateArray())
                    nodes.Add(new Node(Str(n, "id"), Str(n, "type"), n.TryGetProperty("agent", out JsonElement a) ? a.GetString() : null));
            if (p.TryGetProperty("edges", out JsonElement es))
                foreach (JsonElement e in es.EnumerateArray())
                    edges.Add(new Edge(Str(e, "from"), Str(e, "to"), Str(e, "port"), e.TryGetProperty("mode", out JsonElement m) && m.GetString() == "stream"));
            result.Add(new PipelineGraph { Id = Str(p, "id"), Name = Str(p, "name"), Nodes = nodes, Edges = edges });
        }
        return result;
    }

    private static string Str(JsonElement el, string name) => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>What is wrong with this graph, empty when it can run. <paramref name="portsOf"/> returns an agent's input
    /// ports, or null when no such agent is loaded.</summary>
    internal List<string> Validate(Func<string, string[]?> portsOf)
    {
        List<string> problems = new();
        Node? end = Nodes.FirstOrDefault(n => n.Type == "end");
        if (Nodes.FirstOrDefault(n => n.Type == "start") is null) problems.Add("There is no START node.");
        if (end is null) { problems.Add("There is no END node."); return problems; }

        IEnumerable<Edge> intoEnd = Edges.Where(e => e.To == end.Id);
        if (intoEnd.Count() != 1) problems.Add(intoEnd.Any() ? "END can only take one source." : "Nothing is connected to END.");

        foreach (Node n in Nodes.Where(n => n.Type == "agent"))
        {
            string[]? ports = n.Agent is null ? null : portsOf(n.Agent);
            if (ports is null) { problems.Add($"Agent '{n.Agent}' is not loaded (is it enabled, and has ARI been restarted since you created it?)."); continue; }
            foreach (string port in ports)
                if (!Edges.Any(e => e.To == n.Id && e.Port.Equals(port, StringComparison.OrdinalIgnoreCase)))
                    problems.Add($"{n.Agent}: {{{port}}} has nothing wired in.");
        }

        // The agent that answers writes straight into the user's thread, so it has to be the one feeding END; and
        // the built-in Dialogue can only ever be that one (its output is the thread).
        string? replyId = intoEnd.FirstOrDefault()?.From;
        foreach (Node n in Nodes.Where(n => n.Type == "agent" && n.Agent!.Equals("Dialogue", StringComparison.OrdinalIgnoreCase) && n.Id != replyId))
            problems.Add("The built-in Dialogue agent must be the one connected to END.");

        if (Order() is null) problems.Add("The pipeline loops back on itself.");
        return problems;
    }

    /// <summary>Agent nodes grouped into waves: every node in a wave has all its sources in earlier waves, so a wave can run
    /// in parallel. Null if there is a cycle.</summary>
    internal List<List<Node>>? Order()
    {
        HashSet<string> done = new() { StartId };
        foreach (Node n in Nodes.Where(n => n.Type == "start")) done.Add(n.Id);
        List<Node> left = Nodes.Where(n => n.Type == "agent").ToList();
        List<List<Node>> waves = new();
        while (left.Count > 0)
        {
            List<Node> wave = left.Where(n => Edges.Where(e => e.To == n.Id).All(e => done.Contains(e.From))).ToList();
            if (wave.Count == 0) return null;
            wave.ForEach(n => done.Add(n.Id));
            left.RemoveAll(wave.Contains);
            waves.Add(wave);
        }
        return waves;
    }
}

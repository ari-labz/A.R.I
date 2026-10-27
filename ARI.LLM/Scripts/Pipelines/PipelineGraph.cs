using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>One pipeline as the control panel's node editor draws it: START (the conversation) → agents → END (the user).
/// An agent's input ports are fixed by what it is — the built-ins name theirs in <see cref="PipelineGraph.BuiltInPorts"/>,
/// a custom agent gets one per {token} in its prompt. Each port takes exactly one wire.</summary>
internal sealed class PipelineGraph
{
    /// <param name="Type">start, end, agent (a named agent) or pipeline (another pipeline run as a single step).</param>
    internal sealed record Node(string Id, string Type, string? Agent, string? Pipeline = null);
    /// <param name="FromPort">Which output of the source the wire leaves from. Only START has more than one: "history" and "message".</param>
    internal sealed record Edge(string From, string To, string Port, bool Stream, string FromPort = StartHistory);

    internal string       Id    { get; init; } = "";
    internal string       Name  { get; init; } = "";
    internal List<Node>   Nodes { get; init; } = new();
    internal List<Edge>   Edges { get; init; } = new();

    internal const string StartId = "start", EndId = "end";

    /// <summary>The two things every pipeline starts with: the conversation so far, and (separately) the user's latest message.</summary>
    internal const string StartHistory = "history", StartMessage = "message";

    /// <summary>The built-in agents that can sit in a graph, and the input ports each one has. "history" is the START output.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> BuiltInPorts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Memory"]   = ["history"],
        ["Dialogue"] = ["history", "memories"],
    };

    /// <summary>Input ports a graph may leave unwired: the agent then simply runs without that input. Lets a pipeline be
    /// just Dialogue talking straight to the model, with no Memory step.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> OptionalPorts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Dialogue"] = ["memories"],
    };

    internal static bool IsOptional(string agent, string port) =>
        OptionalPorts.TryGetValue(agent, out string[]? o) && o.Contains(port, StringComparer.OrdinalIgnoreCase);

    /// <summary>The pipeline ARI ships and always has: recall memories, then reply with them. <paramref name="withMemory"/> false
    /// gives the same without the recall step, for an install that has no Memory agent.</summary>
    internal static PipelineGraph BuiltInDefault(bool withMemory)
    {
        List<Node> nodes = [new(StartId, "start", null), new("dia", "agent", "Dialogue"), new(EndId, "end", null)];
        List<Edge> edges = [new(StartId, "dia", "history", false), new("dia", EndId, "stream", true)];
        if (withMemory)
        {
            nodes.Insert(1, new Node("mem", "agent", "Memory"));
            edges.Insert(0, new Edge(StartId, "mem", "history", false));
            edges.Insert(2, new Edge("mem", "dia", "memories", false));
        }
        return new PipelineGraph { Id = DefaultId, Name = "Default", Nodes = nodes, Edges = edges };
    }

    internal const string DefaultId = "default";

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
                    nodes.Add(new Node(Str(n, "id"), Str(n, "type"), n.TryGetProperty("agent", out JsonElement a) ? a.GetString() : null,
                        n.TryGetProperty("pipeline", out JsonElement sp) ? sp.GetString() : null));
            if (p.TryGetProperty("edges", out JsonElement es))
                foreach (JsonElement e in es.EnumerateArray())
                    edges.Add(new Edge(Str(e, "from"), Str(e, "to"), Str(e, "port"), e.TryGetProperty("mode", out JsonElement m) && m.GetString() == "stream",
                        Str(e, "fromPort") is { Length: > 0 } fp ? fp : StartHistory));
            result.Add(new PipelineGraph { Id = Str(p, "id"), Name = Str(p, "name"), Nodes = nodes, Edges = edges });
        }
        return result;
    }

    private static string Str(JsonElement el, string name) => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>What is wrong with this graph, empty when it can run. <paramref name="portsOf"/> returns an agent's input
    /// ports, or null when no such agent is loaded.</summary>
    internal List<string> Validate(Func<string, string[]?> portsOf, Func<string, PipelineGraph?> pipelineById, bool asSub = false, HashSet<string>? chain = null)
    {
        List<string> problems = new();
        chain = new HashSet<string>(chain ?? [], StringComparer.OrdinalIgnoreCase) { Id };
        Node? end = Nodes.FirstOrDefault(n => n.Type == "end");
        if (Nodes.FirstOrDefault(n => n.Type == "start") is null) problems.Add("There is no START node.");
        if (end is null) { problems.Add("There is no END node."); return problems; }

        foreach (Edge e in Edges.Where(e => Nodes.Any(n => n.Id == e.From && n.Type == "start")))
            if (!e.FromPort.Equals(StartHistory, StringComparison.OrdinalIgnoreCase) && !e.FromPort.Equals(StartMessage, StringComparison.OrdinalIgnoreCase))
                problems.Add($"START has no '{e.FromPort}' output (it has {StartHistory} and {StartMessage}).");

        IEnumerable<Edge> intoEnd = Edges.Where(e => e.To == end.Id);
        if (intoEnd.Count() != 1) problems.Add(intoEnd.Any() ? "END can only take one source." : "Nothing is connected to END.");

        foreach (Node n in Nodes.Where(n => n.Type == "agent"))
        {
            string[]? ports = n.Agent is null ? null : portsOf(n.Agent);
            if (ports is null) { problems.Add($"Agent '{n.Agent}' is not loaded (is it switched off?)."); continue; }
            foreach (string port in ports.Where(port => !IsOptional(n.Agent!, port)))
                if (!Edges.Any(e => e.To == n.Id && e.Port.Equals(port, StringComparison.OrdinalIgnoreCase)))
                    problems.Add($"{n.Agent}: {{{port}}} has nothing wired in.");
        }

        // The agent that answers writes straight into the user's thread, so it has to be the one feeding END; and
        // the built-in Dialogue can only ever be that one (its output is the thread).
        string? replyId = intoEnd.FirstOrDefault()?.From;
        foreach (Node n in Nodes.Where(n => n.Type == "agent" && n.Agent!.Equals("Dialogue", StringComparison.OrdinalIgnoreCase) && n.Id != replyId))
            problems.Add("The built-in Dialogue agent must be the one connected to END.");

        // Sub-pipelines: what feeds END at the top level is the reply, which is written into the chat, so it has to be an agent.
        // Inside a sub-pipeline nothing can write to the chat, so Dialogue (whose output IS the chat) can't be used there.
        if (!asSub && Nodes.FirstOrDefault(n => n.Id == replyId) is { } feeder)
        {
            if (feeder.Type == "pipeline")
                problems.Add("A sub-pipeline can't be the one connected to END; end with an agent that writes the reply.");
            else if (feeder.Type == "agent" && feeder.Agent!.Equals("Memory", StringComparison.OrdinalIgnoreCase))
                problems.Add("Memory finds notes but can't write a reply; connect an agent that does to END.");
        }
        if (asSub)
            foreach (Node n in Nodes.Where(n => n.Type == "agent" && n.Agent!.Equals("Dialogue", StringComparison.OrdinalIgnoreCase)))
                problems.Add("The built-in Dialogue agent can't be used inside a sub-pipeline.");
        foreach (Node n in Nodes.Where(n => n.Type == "pipeline"))
        {
            PipelineGraph? sub = n.Pipeline is null ? null : pipelineById(n.Pipeline);
            if (sub is null) { problems.Add($"Pipeline '{n.Pipeline}' doesn't exist."); continue; }
            if (chain.Contains(sub.Id)) { problems.Add($"Pipeline '{sub.Name}' ends up running itself."); continue; }
            foreach (string inner in sub.Validate(portsOf, pipelineById, asSub: true, chain))
                problems.Add($"In '{sub.Name}': {inner}");
        }

        if (Order() is null) problems.Add("The pipeline loops back on itself.");
        return problems;
    }

    /// <summary>Agent nodes grouped into waves: every node in a wave has all its sources in earlier waves, so a wave can run
    /// in parallel. Null if there is a cycle.</summary>
    internal List<List<Node>>? Order()
    {
        HashSet<string> done = new() { StartId };
        foreach (Node n in Nodes.Where(n => n.Type == "start")) done.Add(n.Id);
        List<Node> left = Nodes.Where(n => n.Type is "agent" or "pipeline").ToList();
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

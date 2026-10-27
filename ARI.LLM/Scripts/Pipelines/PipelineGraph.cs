using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>One pipeline as the control panel's node editor draws it: START (the conversation) → agents → END (the user).
/// An agent's input ports are fixed by what it is — the built-ins name theirs in <see cref="PipelineGraph.BuiltInPorts"/>,
/// a custom agent gets one per {token} in its prompt. Each port takes exactly one wire.</summary>
internal sealed class PipelineGraph
{
    /// <param name="Type">start, end, agent (a named agent), pipeline (another pipeline run as a single step) or script (a C# script).</param>
    /// <param name="OnError">If set and this node fails, the pipeline carries on with this text as the node's output instead of failing.</param>
    /// <param name="ThreadCode">An agent step with a code keeps its own sub-thread under the chat, found again by that code on every message, so it remembers what it did before.</param>
    internal sealed record Node(string Id, string Type, string? Agent, string? Pipeline = null, string? Script = null, string? OnError = null, string? ThreadCode = null);

    /// <summary>How the validator looks things up: an agent's input ports, another pipeline, a script's ports.</summary>
    internal sealed record Env(Func<string, string[]?> PortsOf, Func<string, PipelineGraph?> PipelineById, Func<string, ScriptShape?> ScriptShape);
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
        // Dialogue only needs the conversation; everything else it takes (memories, and any {token} its prompt adds) may be left unwired.
        agent.Equals("Dialogue", StringComparison.OrdinalIgnoreCase) ? !port.Equals("history", StringComparison.OrdinalIgnoreCase)
            : OptionalPorts.TryGetValue(agent, out string[]? o) && o.Contains(port, StringComparer.OrdinalIgnoreCase);

    /// <summary>The pipeline ARI ships and always has: recall memories, then reply with them. <paramref name="withMemory"/> false
    /// gives the same without the recall step, for an install that has no Memory agent.</summary>
    internal static PipelineGraph BuiltInDefault(bool withMemory, bool memoryAsPipeline = false)
    {
        List<Node> nodes = [new(StartId, "start", null), new("dia", "agent", "Dialogue"), new(EndId, "end", null)];
        List<Edge> edges = [new(StartId, "dia", "history", false), new("dia", EndId, "stream", true)];
        if (withMemory && memoryAsPipeline)
        {
            // Memory recall is its own pipeline (see ShippedJson); if it fails, Dialogue still answers, and says it couldn't remember.
            nodes.Insert(1, new Node("mem", "pipeline", null, Pipeline: MemoryId, OnError: MemoryUnavailableText));
            edges.Insert(1, new Edge("mem", "dia", "memories", false));
        }
        else if (withMemory)
        {
            nodes.Insert(1, new Node("mem", "agent", "Memory"));
            edges.Insert(0, new Edge(StartId, "mem", "history", false));
            edges.Insert(2, new Edge("mem", "dia", "memories", false));
        }
        return new PipelineGraph { Id = DefaultId, Name = "Default", Nodes = nodes, Edges = edges };
    }

    internal const string DefaultId = "default", MemoryId = "memory";

    /// <summary>What Dialogue is told when memory recall fails, so it can say so instead of answering as if it had remembered.</summary>
    internal const string MemoryUnavailableText = "Memory recall is unavailable right now — the memory server appears to be offline. Tell the user at the start of your response that you cannot access your memories at the moment and that your answer may be inaccurate or incomplete as a result. Then respond as best you can.";

    /// <summary>Older saved copies of Default have the Memory agent as a step. This swaps that step for the Memory pipeline in place
    /// (same position, same wire into Dialogue) and returns true if it did. Default pipelines the user changed beyond that are left alone.</summary>
    internal static bool UpgradeLegacyDefault(JsonObject root)
    {
        if (root["pipelines"] is not JsonArray list) return false;
        JsonObject? def = list.OfType<JsonObject>().FirstOrDefault(p => p["id"]?.GetValue<string>() == DefaultId);
        if (def?["nodes"] is not JsonArray nodes || def["edges"] is not JsonArray edges) return false;
        List<JsonObject> boxes = nodes.OfType<JsonObject>().Where(n => n["type"]?.GetValue<string>() is not ("start" or "end")).ToList();
        JsonObject? mem = boxes.FirstOrDefault(n => n["type"]?.GetValue<string>() == "agent" && n["agent"]?.GetValue<string>() == "Memory");
        bool onlyMemoryAndDialogue = boxes.Count == 2 && mem is not null
            && boxes.Any(n => n["type"]?.GetValue<string>() == "agent" && n["agent"]?.GetValue<string>() == "Dialogue");
        if (!onlyMemoryAndDialogue) return false;
        string memId = mem!["id"]!.GetValue<string>();
        mem.Remove("agent");
        mem["type"] = "pipeline";
        mem["pipeline"] = MemoryId;
        mem["onError"] = MemoryUnavailableText;
        foreach (JsonNode? e in edges.Where(e => e?["to"]?.GetValue<string>() == memId).ToList()) edges.Remove(e);   // the sub-pipeline's inputs default to the chat's own
        return true;
    }

    /// <summary>Shipped pipelines other than Default, which can be edited but never deleted.</summary>
    internal const string WalkId = "memory-walk", WalkChatId = "walker-chat";
    internal static readonly string[] ShippedIds = [MemoryId, WalkId, WalkChatId];

    /// <summary>Shipped pipelines that are only steps inside others, so they aren't offered as a way to reply to a chat.</summary>
    internal static readonly string[] StepOnlyIds = [MemoryId, WalkId];

    /// <summary>Pipelines ARI ships besides Default, as the editor's JSON. Memory recall as a pipeline: find candidates (a script), ask
    /// the "Memory Pick" agent which to use, then finish (a script that has the "Memory Extract" and "Memory Privacy" agents do their
    /// part). It is meant to run as a step inside another pipeline.</summary>
    internal const string ShippedJson = """
    { "pipelines": [
      { "id": "memory", "name": "Memory",
        "nodes": [
          { "id": "start",  "type": "start",  "x": 20,  "y": 150 },
          { "id": "find",   "type": "script", "script": "Memory_Find",   "x": 300, "y": 90 },
          { "id": "pick",   "type": "agent",  "agent": "Memory Pick",    "x": 620, "y": 20 },
          { "id": "finish", "type": "script", "script": "Memory_Finish", "x": 920, "y": 150 },
          { "id": "end",    "type": "end",    "x": 1230, "y": 190 }
        ],
        "edges": [
          { "from": "start", "to": "find",   "port": "Message",    "mode": "complete", "fromPort": "message" },
          { "from": "find",  "to": "pick",   "port": "transcript", "mode": "complete", "fromPort": "Transcript" },
          { "from": "find",  "to": "pick",   "port": "candidates", "mode": "complete", "fromPort": "Candidates" },
          { "from": "find",  "to": "finish", "port": "State",      "mode": "complete", "fromPort": "State" },
          { "from": "pick",  "to": "finish", "port": "Picks",      "mode": "complete" },
          { "from": "finish","to": "end",    "port": "stream",     "mode": "stream",   "fromPort": "Block" }
        ] },
      { "id": "memory-walk", "name": "Memory walker",
        "nodes": [
          { "id": "start",  "type": "start",  "x": 20,  "y": 110 },
          { "id": "begin",  "type": "script", "script": "Memory_Start", "x": 300, "y": 70 },
          { "id": "walker", "type": "agent",  "agent": "Memory Walker", "thread": "memory", "x": 640, "y": 50 },
          { "id": "end",    "type": "end",    "x": 980, "y": 110 }
        ],
        "edges": [
          { "from": "start", "to": "begin",  "port": "Message",  "mode": "complete", "fromPort": "message" },
          { "from": "begin", "to": "walker", "port": "starting", "mode": "complete", "fromPort": "Starting" },
          { "from": "begin", "to": "walker", "port": "audience", "mode": "complete", "fromPort": "Audience" },
          { "from": "walker","to": "end",    "port": "stream",   "mode": "stream" }
        ] },
      { "id": "walker-chat", "name": "Memory walker chat",
        "nodes": [
          { "id": "start", "type": "start",    "x": 20,  "y": 140 },
          { "id": "mem",   "type": "pipeline", "pipeline": "memory-walk", "x": 290, "y": 30, "onError": "@memory-unavailable" },
          { "id": "dia",   "type": "agent",    "agent": "Dialogue", "x": 620, "y": 120 },
          { "id": "end",   "type": "end",      "x": 940, "y": 150 }
        ],
        "edges": [
          { "from": "start", "to": "dia", "port": "history",  "mode": "complete", "fromPort": "history" },
          { "from": "mem",   "to": "dia", "port": "memories", "mode": "complete" },
          { "from": "dia",   "to": "end", "port": "stream",   "mode": "stream" }
        ] }
    ] }
    """;

    /// <summary>Tokens a custom agent's prompt can use without wiring: the user's latest message.</summary>
    internal static readonly string[] ImplicitTokens = ["message", "since"];

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
                        n.TryGetProperty("pipeline", out JsonElement sp) ? sp.GetString() : null,
                        n.TryGetProperty("script", out JsonElement sc) ? sc.GetString() : null,
                        n.TryGetProperty("onError", out JsonElement oe) && oe.ValueKind == JsonValueKind.String ? (oe.GetString() == "@memory-unavailable" ? MemoryUnavailableText : oe.GetString()) : null,
                        n.TryGetProperty("thread", out JsonElement tc) && tc.ValueKind == JsonValueKind.String && tc.GetString() is { Length: > 0 } code ? code : null));
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
    internal List<string> Validate(Env env, bool asSub = false, HashSet<string>? chain = null)
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
            string[]? ports = n.Agent is null ? null : env.PortsOf(n.Agent);
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
            if (feeder.Type is "pipeline" or "script")
                problems.Add($"A {(feeder.Type == "script" ? "script" : "sub-pipeline")} can't be the one connected to END; end with an agent that writes the reply.");
            else if (feeder.Type == "agent" && feeder.Agent!.Equals("Memory", StringComparison.OrdinalIgnoreCase))
                problems.Add("Memory finds notes but can't write a reply; connect an agent that does to END.");
        }
        if (asSub)
            foreach (Node n in Nodes.Where(n => n.Type == "agent" && n.Agent!.Equals("Dialogue", StringComparison.OrdinalIgnoreCase)))
                problems.Add("The built-in Dialogue agent can't be used inside a sub-pipeline.");
        foreach (Node n in Nodes.Where(n => n.Type == "pipeline"))
        {
            PipelineGraph? sub = n.Pipeline is null ? null : env.PipelineById(n.Pipeline);
            if (sub is null) { problems.Add($"Pipeline '{n.Pipeline}' doesn't exist."); continue; }
            if (chain.Contains(sub.Id)) { problems.Add($"Pipeline '{sub.Name}' ends up running itself."); continue; }
            foreach (string inner in sub.Validate(env, asSub: true, chain))
                problems.Add($"In '{sub.Name}': {inner}");
        }

        foreach (Node n in Nodes.Where(n => n.Type == "script"))
        {
            ScriptShape? shape = n.Script is null ? null : env.ScriptShape(n.Script);
            if (shape is null) { problems.Add($"Script '{n.Script}' isn't loaded (does it have errors?)."); continue; }
            foreach (string input in shape.Inputs.Where(i => !shape.OptionalInputs.Contains(i, StringComparer.OrdinalIgnoreCase)))
                if (!Edges.Any(e => e.To == n.Id && e.Port.Equals(input, StringComparison.OrdinalIgnoreCase)))
                    problems.Add($"{n.Script}: {{{input}}} has nothing wired in.");
        }

        // A sub-thread code belongs to one agent: two different agents can't share "memory".
        foreach (IGrouping<string, Node> sharing in Nodes.Where(n => n.Type == "agent" && n.ThreadCode is not null).GroupBy(n => n.ThreadCode!, StringComparer.OrdinalIgnoreCase))
        {
            string[] owners = sharing.Select(n => n.Agent!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (owners.Length > 1) problems.Add($"The sub-thread code '{sharing.Key}' is used by {string.Join(" and ", owners)}; each agent needs its own.");
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
        List<Node> left = Nodes.Where(n => n.Type is "agent" or "pipeline" or "script").ToList();
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

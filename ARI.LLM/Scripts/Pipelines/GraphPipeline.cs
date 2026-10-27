using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>Runs the <see cref="PipelineGraph"/> a thread is on (Default unless it was switched). Agents run in waves
/// (everything in a wave has its inputs ready, so a wave runs in parallel); the one wired to END replies into the user's
/// own thread, every other agent runs on a throw-away thread and only hands its text to the ports it is wired to.</summary>
internal sealed class GraphPipeline : Pipeline
{
    private readonly TextingAgent                         textingAgent;
    private readonly Memory?                              memory;
    private readonly Context?                             context;
    private readonly Engram?                              engram;
    private readonly IReadOnlyDictionary<string, CustomAgent> customAgents;

    /// <summary>Finds the graph a thread runs on: the one it was switched to, or Default.</summary>
    private readonly Func<string?, PipelineGraph> graphFor;

    internal event Action<string>? ThreadBufferFull;
    internal event Action<string>? ThreadBecameInactive;

    protected override Agent  PrimaryAgent => textingAgent;
    protected override string PipelineName => "Graph";

    internal GraphPipeline(
        TextingAgent textingAgent, Memory? memory, Context? context, Engram? engram,
        IReadOnlyDictionary<string, CustomAgent> customAgents,
        Func<string?, PipelineGraph> graphFor,
        ConcurrentDictionary<string, CancellationTokenSource> processingThreads,
        ConcurrentDictionary<string, LiveCallInfo>             liveCalls,
        Action<string>                                          notifyWatchers)
        : base(processingThreads, liveCalls, notifyWatchers)
    {
        this.textingAgent = textingAgent;
        this.memory       = memory;
        this.context      = context;
        this.engram       = engram;
        this.customAgents = customAgents;
        this.graphFor     = graphFor;

        textingAgent.ThreadBufferFull     += key => ThreadBufferFull?.Invoke(key);
        textingAgent.ThreadBecameInactive += key => ThreadBecameInactive?.Invoke(key);
    }

    /// <summary>The input ports of an agent that can sit in a graph, or null if it isn't loaded.</summary>
    internal string[]? PortsOf(string agent)
    {
        if (PipelineGraph.BuiltInPorts.TryGetValue(agent, out string[]? ports))
            return agent.Equals("Memory", StringComparison.OrdinalIgnoreCase) && memory is null ? null : ports;
        return customAgents.TryGetValue(agent, out CustomAgent? c) ? PipelineGraph.PromptTokens(c.AgentPrompt).ToArray() : null;
    }

    protected override LiveCallInfo BuildLiveCall(string threadKey) =>
        new("Dialogue", threadKey, 0, textingAgent.BudgetResponse, textingAgent.BudgetContext, textingAgent.BudgetImage);

    protected override async Task<string> RunAsync(
        Thread               thread,
        string               threadKey,
        string               effectivePrompt,
        string               username,
        string?              platformContext,
        Func<string, Task>?  onDelta,
        CancellationTokenSource cts,
        string?              localPath,
        Func<string, Task>?  onTextDelta = null)
    {
        PipelineGraph graph = graphFor(thread.GraphId);

        if (engram?.IsSweeping(threadKey) == true)
        {
            Shared.Logger.LogInformation("[Graph] ({Thread}) waiting for Engram sweep to finish before processing.", threadKey);
            await engram.WaitForSweep(threadKey, cts.Token);
        }
        Shared.Logger.LogInformation("[Graph:{Graph}] ({Thread}) prompt\n\"{Prompt}\"", graph.Name, threadKey, effectivePrompt);

        string? contextSummary = context?.GetContext(threadKey);
        PrivacyMode privacy    = thread.IsGuarded ? PrivacyMode.Guarded : PrivacyMode.Unrestricted;
        string historyText     = FormatHistory(thread.GetChatHistory());

        // Null is a real value: "this agent had nothing to say" (Memory with no access) — the Dialogue agent leaves its memory block out for null but writes "None" for "".
        ConcurrentDictionary<string, string?> outputs = new();
        // START has two outputs: the conversation so far, and the latest message on its own.
        foreach (PipelineGraph.Node start in graph.Nodes.Where(n => n.Type == "start"))
        {
            outputs[$"{start.Id}:{PipelineGraph.StartHistory}"] = historyText;
            outputs[$"{start.Id}:{PipelineGraph.StartMessage}"] = effectivePrompt;
        }

        string replyId = graph.Edges.First(e => graph.Nodes.First(n => n.Id == e.To).Type == "end").From;
        string? reply  = null;
        double? recallSeconds = null;

        foreach (List<PipelineGraph.Node> wave in graph.Order()!)
        {
            string?[] results = await Task.WhenAll(wave.Select(node => RunNode(node)));
            for (int i = 0; i < wave.Count; i++)
            {
                if (wave[i].Id == replyId) reply = results[i];
                else outputs[wave[i].Id] = results[i];
            }
        }
        return reply ?? "";

        async Task<string?> RunNode(PipelineGraph.Node node)
        {
            Dictionary<string, string?> inputs = new(StringComparer.OrdinalIgnoreCase);
            foreach (PipelineGraph.Edge e in graph.Edges.Where(e => e.To == node.Id))
                inputs[e.Port] = outputs.TryGetValue(OutputKey(graph, e), out string? v) ? v : null;
            bool isReply = node.Id == replyId;
            string agentName = node.Agent!;
            Stopwatch sw = Stopwatch.StartNew();

            if (agentName.Equals("Memory", StringComparison.OrdinalIgnoreCase))
                return await Recall();

            if (agentName.Equals("Dialogue", StringComparison.OrdinalIgnoreCase))
            {
                inputs.TryGetValue("memories", out string? mem);
                return await textingAgent.Prompt(thread, effectivePrompt, new PromptOptions
                {
                    Username            = username,
                    RecallNotes         = mem,
                    RecallSeconds       = recallSeconds,
                    ContextSummary      = contextSummary,
                    Ct                  = cts.Token,
                    UserMessagePreadded = true,
                    OnDelta             = onDelta,
                });
            }

            CustomAgent agent = customAgents[agentName];
            inputs["message"] = effectivePrompt;
            using IDisposable _ = CustomAgent.Resolve(PipelineGraph.Fill(agent.AgentPrompt, inputs.ToDictionary(kv => kv.Key, kv => kv.Value ?? "", StringComparer.OrdinalIgnoreCase)));
            string result;
            if (isReply)
            {
                result = await agent.Prompt(thread, effectivePrompt, new PromptOptions
                {
                    Username = username, ContextSummary = contextSummary, Ct = cts.Token, UserMessagePreadded = true, OnDelta = onDelta,
                });
            }
            else
            {
                Thread scratch = new(ThreadPipeline.Dialogue, $"graph:{Guid.NewGuid():N}") { Internal = true, ReadOnlyTools = true };
                new ListTools(scratch).Register(scratch);
                new RequestTools(scratch).Register(scratch);
                result = await agent.Prompt(scratch, effectivePrompt, new PromptOptions { Username = username, Ct = cts.Token });
            }
            Shared.Logger.LogInformation("[Graph:{Graph}] ({Thread}) {Agent} finished in {Sec:F1}s.", graph.Name, threadKey, agentName, sw.Elapsed.TotalSeconds);
            return result;

            // Same recall the Dialogue pipeline does: skipped without access, and a failed recall never blocks the reply.
            async Task<string?> Recall()
            {
                if (memory is null || !thread.HasMemoryAccess) return null;
                Stopwatch recallSw = Stopwatch.StartNew();
                try { string? notes = await memory.GetNotes(thread.GetChatHistory(), effectivePrompt, contextSummary, cts.Token, privacy); recallSeconds = recallSw.Elapsed.TotalSeconds; return notes; }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Shared.Logger.LogWarning(ex, "[Memory] ({Thread}) Memory recall failed — continuing without memories. {Error}", threadKey, ex.Message);
                    recallSeconds = recallSw.Elapsed.TotalSeconds;
                    return "Memory recall is unavailable right now — the memory server appears to be offline. Tell the user at the start of your response that you cannot access your memories at the moment and that your answer may be inaccurate or incomplete as a result. Then respond as best you can.";
                }
            }
        }
    }

    /// <summary>Where a wire's value is stored: a node's output, or for START the specific output the wire leaves from.</summary>
    private static string OutputKey(PipelineGraph graph, PipelineGraph.Edge e) =>
        graph.Nodes.Any(n => n.Id == e.From && n.Type == "start") ? $"{e.From}:{e.FromPort.ToLowerInvariant()}" : e.From;

    private static string FormatHistory(List<ThreadMessage> history)
    {
        StringBuilder sb = new();
        foreach (ThreadMessage m in history) sb.Append(m.Username).Append(": ").AppendLine(m.Content);
        return sb.ToString().TrimEnd();
    }
}

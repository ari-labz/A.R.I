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
    private readonly Dialogue                             textingAgent;   // the reply agent: texting for chats, talking for voice
    private readonly Memory?                              memory;
    private readonly Context?                             context;
    private readonly Engram?                              engram;
    private readonly IReadOnlyDictionary<string, CustomAgent> customAgents;

    /// <summary>Finds the graph a thread runs on: the one it was switched to, or Default.</summary>
    private readonly Func<string?, PipelineGraph> graphFor;

    /// <summary>Finds a pipeline by id, so one can run inside another.</summary>
    private readonly Func<string, PipelineGraph?> pipelineById;

    private const int MaxDepth = 8;

    /// <summary>The sub-thread code Recall keeps under a chat when the graph doesn't name one.</summary>
    private const string RECALL_CODE = "memory";

    /// <summary>A ceiling on Recall's block, a little over the ~200 words its prompt asks for: at temperature 0.8 one run in a few copied its
    /// own growing blocks up to 650 words, 15 s a turn.</summary>
    private const int RECALL_MAX_TOKENS = 450;

    /// <summary>Tokens Dialogue's prompt may use that aren't extra inputs: the memory block (either spelling) and the two it always has.</summary>
    private static readonly string[] DialogueOwnTokens = ["history", "memories", "memory"];


    internal event Action<string>? ThreadBufferFull;
    internal event Action<string>? ThreadBecameInactive;

    protected override Agent  PrimaryAgent => textingAgent;
    protected override string PipelineName => "Graph";

    internal GraphPipeline(
        Dialogue textingAgent, Memory? memory, Context? context, Engram? engram,
        IReadOnlyDictionary<string, CustomAgent> customAgents,
        Func<string?, PipelineGraph> graphFor,
        Func<string, PipelineGraph?> pipelineById,
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
        this.pipelineById = pipelineById;

        textingAgent.ThreadBufferFull     += key => ThreadBufferFull?.Invoke(key);
        textingAgent.ThreadBecameInactive += key => ThreadBecameInactive?.Invoke(key);
    }

    /// <summary>The input ports of an agent that can sit in a graph, or null if it isn't loaded.</summary>
    internal string[]? PortsOf(string agent)
    {
        if (PipelineGraph.BuiltInPorts.TryGetValue(agent, out string[]? ports))
        {
            if (agent.Equals("Memory", StringComparison.OrdinalIgnoreCase) && memory is null) return null;
            // Dialogue's own prompt can hold {tokens} too: each one beyond its fixed ports becomes an input.
            if (agent.Equals("Dialogue", StringComparison.OrdinalIgnoreCase))
                return ports.Concat(PipelineGraph.PromptTokens(textingAgent.AgentPrompt).Where(t => !DialogueOwnTokens.Contains(t, StringComparer.OrdinalIgnoreCase))).ToArray();
            return ports;
        }
        return customAgents.TryGetValue(agent, out CustomAgent? c) ? PipelineGraph.PromptTokens(c.AgentPrompt + "\n" + c.MessagePrompt).ToArray() : null;
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

        if (engram?.BlocksReply(threadKey) == true)
        {
            Shared.Logger.LogInformation("[Graph] ({Thread}) waiting for Engram sweep to finish before processing.", threadKey);
            thread.WaitingOnMemory = true; notifyWatchers(threadKey);
            try { await engram.WaitForSweep(threadKey, cts.Token); }
            finally { thread.WaitingOnMemory = false; notifyWatchers(threadKey); }
        }
        Shared.Logger.LogInformation("[Graph:{Graph}] ({Thread}) prompt\n\"{Prompt}\"", graph.Name, threadKey, effectivePrompt);

        string? contextSummary = context?.GetContext(threadKey);
        PrivacyMode privacy    = thread.IsGuarded ? PrivacyMode.Guarded : PrivacyMode.Unrestricted;
        string historyText     = FormatHistory(thread.GetChatHistory());

        double? recallSeconds = null;

        string? top = await RunGraph(graph, historyText, effectivePrompt, isTop: true, depth: 0);
        return top ?? "";

        // One pass over a graph: START's two outputs go in, whatever feeds END comes out. At the top level that last agent
        // writes straight into the user's thread; inside a sub-pipeline it can't (its result is handed to the node that called
        // it), so every agent there runs on a throw-away thread.
        async Task<string?> RunGraph(PipelineGraph graph, string history, string message, bool isTop, int depth)
        {
            if (depth > MaxDepth) throw new InvalidOperationException($"Sub-pipelines are nested more than {MaxDepth} deep.");

            // Null is a real value: "this agent had nothing to say" (Memory with no access) — the Dialogue agent leaves its memory block out for null but writes "None" for "".
            ConcurrentDictionary<string, string?> outputs = new();
            // START has two outputs: the conversation so far, and the latest message on its own.
            foreach (PipelineGraph.Node start in graph.Nodes.Where(n => n.Type == "start"))
            {
                outputs[$"{start.Id}:{PipelineGraph.StartHistory}"] = history;
                outputs[$"{start.Id}:{PipelineGraph.StartMessage}"] = message;
            }

            string feederId = graph.Edges.First(e => graph.Nodes.First(n => n.Id == e.To).Type == "end").From;
            string? result = null;

            foreach (List<PipelineGraph.Node> wave in graph.Order()!)
            {
                string?[] results = await Task.WhenAll(wave.Select(node => RunNode(node)));
                for (int i = 0; i < wave.Count; i++)
                {
                    if (wave[i].Id == feederId) result = results[i];
                    outputs[wave[i].Id] = results[i];
                }
            }
            return result;

            // A node that fails can carry on with its onError text instead of taking the whole reply down with it.
            async Task<string?> RunNode(PipelineGraph.Node node)
            {
                try { return await RunNodeCore(node); }
                catch (Exception ex) when (node.OnError is not null && !cts.IsCancellationRequested)
                {
                    Shared.Logger.LogWarning(ex, "[Graph:{Graph}] ({Thread}) {Node} failed — carrying on with its fallback text. {Error}", graph.Name, threadKey, node.Agent ?? node.Pipeline ?? node.Id, ex.Message);
                    return node.OnError;
                }
            }

            async Task<string?> RunNodeCore(PipelineGraph.Node node)
            {
                Dictionary<string, string?> inputs = new(StringComparer.OrdinalIgnoreCase);
                foreach (PipelineGraph.Edge e in graph.Edges.Where(e => e.To == node.Id))
                    inputs[e.Port] = outputs.TryGetValue(OutputKey(graph, e), out string? v) ? v : null;
                bool isReply = isTop && node.Id == feederId;

                // A sub-pipeline is one node: the same two inputs START has, each defaulting to what this pipeline itself received.
                if (node.Type == "pipeline")
                {
                    PipelineGraph sub = pipelineById(node.Pipeline!) ?? throw new InvalidOperationException($"Pipeline '{node.Pipeline}' no longer exists.");
                    Stopwatch subSw = Stopwatch.StartNew();
                    string? subResult = await RunGraph(sub, inputs.GetValueOrDefault(PipelineGraph.StartHistory) ?? history, inputs.GetValueOrDefault(PipelineGraph.StartMessage) ?? message, isTop: false, depth + 1);
                    Shared.Logger.LogInformation("[Graph:{Graph}] ({Thread}) sub-pipeline '{Sub}' finished in {Sec:F1}s.", graph.Name, threadKey, sub.Name, subSw.Elapsed.TotalSeconds);
                    return subResult;
                }

                string agentName = node.Agent!;
                Stopwatch sw = Stopwatch.StartNew();

                // Recall: the Memory step traverses the brain with tools, on its own sub-thread under the chat. Code works out who is
                // listening and which notes to start from (names only); the Recall agent opens them, follows links, and writes the block.
                string? threadCode = node.ThreadCode;
                if (agentName.Equals("Memory", StringComparison.OrdinalIgnoreCase))
                {
                    thread.MemoryDeltaThisTurn = false;
                    if (!customAgents.TryGetValue(SystemAgents.Walker, out CustomAgent? walker)) return await Recall();
                    if (memory is null || !thread.HasMemoryAccess) return null;
                    threadCode ??= RECALL_CODE;
                    Thread recall = thread.GetOrCreateChild(threadCode, walker.Name, () => NewOwnThread(thread, threadCode, walker));
                    StartingPoints start = new MemoryApi(memory, thread, contextSummary, privacy).Start(message, recall);
                    if (start.Text is null) return null;
                    inputs["starting"] = start.Text;
                    inputs["audience"] = start.Audience;
                    // Every block sent since the window last moved is still in Dialogue's view, so ask only for what's new.
                    thread.MemoryDeltaThisTurn = thread.MemoryBlockStart >= 0 && thread.MemoryBlockStart == thread.ContextStart;
                    inputs["task"] = (start.UnreadLeads.Count > 0 ? SystemAgents.WalkerUnreadHint(start.UnreadLeads) : "")
                                     + (thread.MemoryDeltaThisTurn ? SystemAgents.WalkerDeltaTask : SystemAgents.WalkerFullTask);
                    agentName  = SystemAgents.Walker;
                }

                if (agentName.Equals("Dialogue", StringComparison.OrdinalIgnoreCase))
                {
                    inputs.TryGetValue("memories", out string? mem);
                    // Dialogue's prompt can place the memory itself with {memory} and take extra inputs as {tokens}; without those it works as it always has.
                    Dictionary<string, string> dialogueTokens = inputs.ToDictionary(kv => kv.Key, kv => kv.Value ?? "", StringComparer.OrdinalIgnoreCase);
                    dialogueTokens["memory"] = dialogueTokens["memories"] = mem?.Trim() ?? "";
                    foreach (string extra in PortsOf("Dialogue") ?? []) dialogueTokens.TryAdd(extra, "");   // an input left unwired reads as empty, not as the literal {token}
                    bool placesMemory = System.Text.RegularExpressions.Regex.IsMatch(textingAgent.AgentPrompt, @"\{(memory|memories)\}");
                    Agent.PromptTokens.Value = dialogueTokens;
                    try
                    {
                        return await textingAgent.Prompt(thread, effectivePrompt, new PromptOptions
                        {
                            Username            = username,
                            RecallNotes         = mem,
                            MemoryInPrompt      = placesMemory,
                            RecallSeconds       = recallSeconds,
                            ContextSummary      = contextSummary,
                            Ct                  = cts.Token,
                            UserMessagePreadded = true,
                            OnDelta             = onDelta,
                            OnTextDelta         = onTextDelta,
                        });
                    }
                    finally { Agent.PromptTokens.Value = null; }
                }

                CustomAgent agent = customAgents[agentName];

                // Something upstream had nothing to hand over (Memory finding nothing, say): an agent behind the scenes has nothing to work on, so it doesn't run.
                if (!isReply && inputs.Values.Any(v => v is null))
                {
                    Shared.Logger.LogInformation("[Graph:{Graph}] ({Thread}) {Agent} skipped: an input was empty.", graph.Name, threadKey, agentName);
                    return null;
                }
                inputs["message"] = message;
                Dictionary<string, string> filled = inputs.ToDictionary(kv => kv.Key, kv => kv.Value ?? "", StringComparer.OrdinalIgnoreCase);

                // An agent with a sub-thread code keeps its own thread under this chat, found again by that code on every message, so it
                // remembers what it did before. Only one run uses it at a time, and a run that is cancelled leaves nothing half-finished.
                Thread? own = null;
                if (!isReply && threadCode is { } code)
                {
                    own = thread.GetOrCreateChild(code, agentName, () => NewOwnThread(thread, code, agent));
                    await own.RunLock.WaitAsync(cts.Token);
                }
                string result;
                try
                {
                    int checkpoint = own?.History.Count ?? 0;
                    List<ThreadMessage> chat = thread.GetChatHistory();
                    filled["since"] = own is null ? history : SinceText(chat, own);
                    using IDisposable _ = CustomAgent.Resolve(PipelineGraph.Fill(agent.AgentPrompt, filled));
                    string userText = string.IsNullOrWhiteSpace(agent.MessagePrompt) ? message : PipelineGraph.Fill(agent.MessagePrompt, filled);
                    if (agentName == SystemAgents.Walker && inputs.GetValueOrDefault("task") is { } task)
                        userText = WithTask(agent, userText, task);
                    if (isReply)
                    {
                        // The reply agent answers the user's own message, which is already in the thread; a message template only applies to steps behind the scenes.
                        result = await agent.Prompt(thread, message, new PromptOptions
                        {
                            Username = username, ContextSummary = contextSummary, Ct = cts.Token, UserMessagePreadded = true, OnDelta = onDelta,
                        });
                    }
                    else
                    {
                        if (agentName == SystemAgents.Walker) agent.OnStepCompletePipeline ??= AskToWriteUp;
                        int maxTokens = agentName == SystemAgents.Walker ? RECALL_MAX_TOKENS : 0;
                        try { result = await agent.Prompt(own ?? NewScratch(agent), userText, new PromptOptions { Username = username, Ct = cts.Token, MaxTokensOverride = maxTokens }); }
                        catch
                        {
                            if (own is not null && own.History.Count > checkpoint) own.History.RemoveRange(checkpoint, own.History.Count - checkpoint);
                            throw;
                        }
                        // A step that used tools comes back with the chat's display markers in its text; what the next box receives is plain text.
                        result = System.Text.RegularExpressions.Regex.Replace(result, @"<!--ari-[^>]*-->", "").Trim();
                        if (agentName == SystemAgents.Walker) result = WithoutEcho(result, username);
                        // A "Nothing new." left in Recall's thread is what it copies next time (measured: once it said it at turn 2 it said it every
                        // turn after, even having just read the note the message was about). Without it, the message it skipped is simply still
                        // in what it's told next time, since the chat count stays put.
                        bool forgettable = agentName == SystemAgents.Walker && Agent.IsNothingNew(result)
                                           && own is not null && OpenedNotes(own.History.Skip(checkpoint)).Count == 0;
                        if (forgettable) own!.History.RemoveRange(checkpoint, own.History.Count - checkpoint);
                        else if (own is not null) own.LastChatCount = chat.Count;
                    }
                }
                finally { own?.RunLock.Release(); }
                Shared.Logger.LogInformation("[Graph:{Graph}] ({Thread}) {Agent} finished in {Sec:F1}s.", graph.Name, threadKey, agentName, sw.Elapsed.TotalSeconds);
                return result;

                // Same recall the Dialogue pipeline does: skipped without access, and a failed recall never blocks the reply.
                async Task<string?> Recall()
                {
                    if (memory is null || !thread.HasMemoryAccess) return null;
                    Stopwatch recallSw = Stopwatch.StartNew();
                    try { string? notes = await memory.GetNotes(thread.GetChatHistory(), message, contextSummary, cts.Token, privacy); recallSeconds = recallSw.Elapsed.TotalSeconds; return notes; }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Shared.Logger.LogWarning(ex, "[Memory] ({Thread}) Memory recall failed — continuing without memories. {Error}", threadKey, ex.Message);
                        recallSeconds = recallSw.Elapsed.TotalSeconds;
                        return PipelineGraph.MemoryUnavailableText;
                    }
                }
            }
        }
    }

    // ── Recall ───────────────────────────────────────────────────────────────────

    /// <summary>Recall's message with this run's task in it. Messages saved before {task} existed end with the full-block instruction instead.</summary>
    private static string WithTask(CustomAgent walker, string userText, string task)
    {
        if (walker.MessagePrompt?.Contains("{task}") == true) return userText;
        return userText.Contains(SystemAgents.WalkerFullTask)
            ? userText.Replace(SystemAgents.WalkerFullTask, task)
            : $"{userText}\n\n{task}";
    }

    /// <summary>Recall's block without the speaker tag ("ARI:", "Sam:") it sometimes copies from the history it's shown.</summary>
    private static string WithoutEcho(string block, string username)
    {
        string speakers = $"ARI|{System.Text.RegularExpressions.Regex.Escape(username)}";
        return System.Text.RegularExpressions.Regex.Replace(block, $@"^\s*(?:{speakers})\s*:\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>Notes these thread items actually read (not the "already open" reminders), by title.</summary>
    private static List<string> OpenedNotes(IEnumerable<ThreadItem> items)
    {
        List<string> titles = new();
        foreach (Response response in items.OfType<Response>())
            foreach (TraceStep step in response.Trace?.ToList() ?? [])
                if (step is { Kind: "tool_result", Name: "recall_memory" } && step.Text?.StartsWith("# ") == true)
                    titles.Add(step.Text[2..].Split('\n')[0].Trim());
        return titles.Distinct().ToList();
    }

    /// <summary>Recall opened notes for this message and then said "Nothing new.": it's asked, once, to write up what it read.</summary>
    private static string? AskToWriteUp(Thread recall, string stepText, bool hadTools)
    {
        if (!Agent.IsNothingNew(stepText) || recall.streamingResponse is not { } turn || recall.WriteUpAsked == turn) return null;
        List<string> opened = OpenedNotes([turn]);
        if (opened.Count == 0) return null;
        recall.WriteUpAsked = turn;
        return SystemAgents.WalkerWriteUp(opened);
    }

    /// <summary>Reads Recall's opening (its prompt and tools) into its slot at startup, so the first conversation doesn't spend seconds on it.</summary>
    internal async Task WarmRecallAsync(string username)
    {
        if (memory is null || !customAgents.TryGetValue(SystemAgents.Walker, out CustomAgent? walker)) return;
        Thread chat = new(ThreadPipeline.Dialogue, "warmup-recall") { Internal = true, HasMemoryAccess = true };
        chat.History.Add(new Prompt { AuthorName = username, Text = ".", Timestamp = DateTime.Now });
        Thread recall = chat.GetOrCreateChild(RECALL_CODE, walker.Name, () => NewOwnThread(chat, RECALL_CODE, walker));
        StartingPoints start = new MemoryApi(memory, chat, null, PrivacyMode.Unrestricted).Start(".", recall);
        Dictionary<string, string> filled = new(StringComparer.OrdinalIgnoreCase)
        {
            ["audience"] = start.Audience, ["since"] = $"{username}: .", ["starting"] = start.Text ?? "", ["task"] = SystemAgents.WalkerFullTask, ["message"] = ".",
        };
        using IDisposable _ = CustomAgent.Resolve(PipelineGraph.Fill(walker.AgentPrompt, filled));
        await walker.Prompt(recall, PipelineGraph.Fill(walker.MessagePrompt ?? "", filled), new PromptOptions { Username = username, WarmOnly = true });
    }

    /// <summary>A throw-away thread for one run of a step: read-only tools, and any tool groups the agent starts with.</summary>
    private static Thread NewScratch(CustomAgent agent)
    {
        Thread scratch = new(ThreadPipeline.Dialogue, $"graph:{Guid.NewGuid():N}") { Internal = true, ReadOnlyTools = true };
        EquipThread(scratch, agent);
        return scratch;
    }

    /// <summary>A step's own sub-thread under the chat. Internal, so it's off the chat list and Engram leaves it alone, but it shows
    /// under its parent in the thread inspector.</summary>
    private static Thread NewOwnThread(Thread parent, string code, CustomAgent agent)
    {
        Thread child = new(ThreadPipeline.Dialogue, $"{parent.Key}:{code}")
        {
            Internal = true, ReadOnlyTools = true, KeepToolHistory = true, KeepToolResults = true, Parent = parent, Code = code, OwnerAgent = agent.Name, Label = $"{agent.Name} ({code})",
        };
        EquipThread(child, agent);
        return child;
    }

    private static void EquipThread(Thread thread, CustomAgent agent)
    {
        new ListTools(thread).Register(thread);
        new RequestTools(thread).Register(thread);
        foreach (string group in agent.PreloadedTools ?? [])
            ToolFactories.LoadGroup(group, thread);
    }

    /// <summary>What was said in the chat since this sub-thread last looked: ARI's last reply and the new message. On its first look, the last few messages.</summary>
    private static string SinceText(List<ThreadMessage> chat, Thread sub)
    {
        const int FirstLook = 12;
        IEnumerable<ThreadMessage> fresh = sub.History.Count == 0 || sub.LastChatCount > chat.Count ? chat.TakeLast(FirstLook) : chat.Skip(sub.LastChatCount);
        return string.Join("\n", fresh.Select(m => $"{m.Username}: {m.Content}"));
    }

    /// <summary>Where a wire's value is stored: a node's output, or for START the specific output (history or message) the wire leaves from.</summary>
    private string OutputKey(PipelineGraph graph, PipelineGraph.Edge e)
    {
        PipelineGraph.Node? source = graph.Nodes.FirstOrDefault(n => n.Id == e.From);
        if (source?.Type == "start") return $"{e.From}:{e.FromPort.ToLowerInvariant()}";
        return e.From;
    }

    private static string FormatHistory(List<ThreadMessage> history)
    {
        StringBuilder sb = new();
        foreach (ThreadMessage m in history) sb.Append(m.Username).Append(": ").AppendLine(m.Content);
        return sb.ToString().TrimEnd();
    }
}

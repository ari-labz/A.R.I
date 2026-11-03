using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ARI.BrainVault;
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

    /// <summary>How long a live message waits for Recall's read-ahead to finish before stopping it. Stopping one costs more than it saves:
    /// its half-done turn is dropped, so the 9B's cache no longer matches and the live run re-reads ~10k tokens (measured: 11 s).</summary>
    private static readonly TimeSpan PREFETCH_GRACE = TimeSpan.FromSeconds(12);

    /// <summary>One read-ahead per message being typed, once it is long enough to say what it's about (measured: an uncapped draft read
    /// ahead ~6 times per message, and those prompts grew Recall's thread to 15–19k tokens).</summary>
    private const int MAX_DRAFT_READS = 1;
    private const int DRAFT_MIN_WORDS = 8;

    /// <summary>How long a first message's draft read-ahead stays waiting for the chat that message will start.</summary>
    private static readonly TimeSpan DRAFT_KEPT_FOR = TimeSpan.FromMinutes(10);

    private const int DRAFT_WORD_MIN_LENGTH = 3;

    /// <summary>A ceiling on Recall's block, a little over the ~200 words its prompt asks for: at temperature 0.8 one run in a few copied its
    /// own growing blocks up to 650 words, 15 s a turn.</summary>
    private const int RECALL_MAX_TOKENS = 450;

    /// <summary>Recall reading ahead on a first message that hasn't been sent, by the user typing it: the chat it will start doesn't exist yet.</summary>
    private readonly ConcurrentDictionary<string, (Thread Chat, DateTime Started)> draftChats = new();

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
        Thread? recallThread  = null;

        string? top = await RunGraph(graph, historyText, effectivePrompt, isTop: true, depth: 0);
        if (recallThread is not null && top is { Length: > 0 } && RecallExperiment.LookAhead)
            LookAhead(thread, recallThread, username, $"{effectivePrompt}\n{top}");
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
                    Thread recall = RecallThreadOf(thread, threadCode, walker, username);
                    await recall.SettlePrefetch(PREFETCH_GRACE);
                    StartingPoints start = new MemoryApi(memory, thread, contextSummary, privacy).Start(message, recall);
                    if (start.Text is null) return null;
                    recallThread = recall;
                    inputs["starting"] = start.Text;
                    inputs["audience"] = start.Audience;
                    // Every block sent since the window last moved is still in Dialogue's view, so ask only for what's new.
                    thread.MemoryDeltaThisTurn = thread.MemoryBlockStart >= 0 && thread.MemoryBlockStart == thread.ContextStart;
                    inputs["task"] = (RecallExperiment.Hint && start.UnreadLeads.Count > 0 ? SystemAgents.WalkerUnreadHint(start.UnreadLeads) : "")
                                     + (!thread.MemoryDeltaThisTurn ? SystemAgents.WalkerFullTask
                                        : RecallExperiment.StrictDelta ? SystemAgents.WalkerDeltaTaskStrict : SystemAgents.WalkerDeltaTask)
                                     + (RecallExperiment.OldTask ? SystemAgents.WalkerOldLengthRule : "");
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
                        bool forgettable = agentName == SystemAgents.Walker && RecallExperiment.DropNothingNew && Agent.IsNothingNew(result)
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

    // ── Recall's own thread, and reading ahead on it ─────────────────────────────

    /// <summary>Recall's sub-thread under a chat: the one it already has, the one it started on this chat's first message while it was still
    /// being typed, or a new one with ARI's and the speaker's notes already open.</summary>
    private Thread RecallThreadOf(Thread chat, string code, CustomAgent walker, string username)
    {
        if (chat.FindChild(code) is { } existing) return existing;
        // Internal chats (the draft stand-ins themselves, warm-up) never take one over.
        if (!chat.Internal && chat.OwnerId is { } owner && chat.History.OfType<Prompt>().Count() <= 1
            && draftChats.TryGetValue(owner, out (Thread Chat, DateTime Started) draft)
            && DateTime.UtcNow - draft.Started < DRAFT_KEPT_FOR && draft.Chat.FindChild(code) is { } readAhead
            && draftChats.TryRemove(owner, out _))
        {
            // Everything the draft's chat said is what this chat's first message says, so Recall is told about the whole chat from the start.
            readAhead.LastChatCount = 0;
            chat.AdoptChild(readAhead);
            Shared.Logger.LogInformation("[Recall] ({Thread}) took over the reading done while the first message was typed.", chat.Key);
            return readAhead;
        }
        Thread recall = chat.GetOrCreateChild(code, walker.Name, () => NewOwnThread(chat, code, walker));
        if (RecallExperiment.Preload && recall.OpenNotes is null) recall.OpenNotes = PreloadedNotes(username);
        return recall;
    }

    /// <summary>ARI's own note and the speaker's, which Recall opened at the start of nearly every conversation. They're the same for every
    /// conversation with the same person, so after the first one the model reads them from its cache instead of again.</summary>
    private static string? PreloadedNotes(string username)
    {
        List<string> shown = new();
        Note? self = Brain.GetNote("ARI");
        if (self is { } ari && ari.Content.Trim().Length > 0) shown.Add(RecallMemory.Show(ari, ari.Content));
        if (Brain.GetNote(username) is { } speaker && speaker.Name != self?.Name && speaker.Content.Trim().Length > 0) shown.Add(RecallMemory.Show(speaker, speaker.Content));
        return shown.Count == 0 ? null : $"NOTES ALREADY OPEN (read them as if you had opened them yourself):\n\n{string.Join("\n\n", shown)}";
    }

    /// <summary>Recall's message with this run's task in it. Messages saved before {task} existed end with the full-block instruction instead.</summary>
    private static string WithTask(CustomAgent walker, string userText, string task)
    {
        if (walker.MessagePrompt?.Contains("{task}") == true) return userText;
        return userText.Contains(SystemAgents.WalkerFullTask)
            ? userText.Replace(SystemAgents.WalkerFullTask, task)
            : $"{userText}\n\n{task}";
    }

    /// <summary>Recall's block without what it copies from its own history: a speaker tag ("ARI:", "Sam:") and the "Ready." its read-aheads
    /// end with, which it sometimes starts a real block with.</summary>
    private static string WithoutEcho(string block, string username)
    {
        string speakers = $"ARI|{System.Text.RegularExpressions.Regex.Escape(username)}";
        string cleaned = System.Text.RegularExpressions.Regex.Replace(block, $@"^\s*(?:(?:{speakers})\s*:\s*)?(?:{System.Text.RegularExpressions.Regex.Escape(SystemAgents.Ready)}\s*)?", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return cleaned;
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
        if (!RecallExperiment.DropNothingNew || !Agent.IsNothingNew(stepText) || recall.streamingResponse is not { } turn || recall.WriteUpAsked == turn) return null;
        List<string> opened = OpenedNotes([turn]);
        if (opened.Count == 0) return null;
        recall.WriteUpAsked = turn;
        return SystemAgents.WalkerWriteUp(opened);
    }

    /// <summary>ARI has replied: while the person reads it, Recall opens what the next message is likely to need.</summary>
    private void LookAhead(Thread chat, Thread recall, string username, string exchange)
    {
        if (recall.Prefetch is not null || !customAgents.TryGetValue(SystemAgents.Walker, out CustomAgent? walker)) return;
        string since = SinceText(chat.GetChatHistory(), recall);
        string searchText = System.Text.RegularExpressions.Regex.Replace(exchange, @"<!--ari-[^>]*-->", "");
        StartReadAhead(chat, recall, walker, username, since, searchText, SystemAgents.WalkerLookAheadTask, advancesChat: true);
    }

    /// <summary>The person is typing in a chat: Recall opens what the message will need before it's sent. A draft that arrives while Recall
    /// is already reading ahead runs after it, and one that brings no new words isn't read for at all.</summary>
    internal void PrefetchDraft(Thread chat, string draft, string username)
    {
        if (!RecallExperiment.Drafts || memory is null || !chat.HasMemoryAccess) return;
        if (draft.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < DRAFT_MIN_WORDS) return;
        if (!customAgents.TryGetValue(SystemAgents.Walker, out CustomAgent? walker)) return;
        PipelineGraph.Node? memoryNode = graphFor(chat.GraphId).Nodes.FirstOrDefault(n => "Memory".Equals(n.Agent, StringComparison.OrdinalIgnoreCase));
        if (memoryNode is null) return;
        Thread recall = RecallThreadOf(chat, memoryNode.ThreadCode ?? RECALL_CODE, walker, username);
        if (recall.DraftReads >= MAX_DRAFT_READS) return;
        if (recall.Prefetch is not null) { recall.PendingDraft = draft; return; }
        if (!BringsNewWords(draft, recall.LastDraft)) return;
        recall.LastDraft = draft;
        recall.DraftReads++;
        StartReadAhead(chat, recall, walker, username, $"{username} (still typing, not sent yet): {draft}", draft, SystemAgents.WalkerDraftTask, advancesChat: false);
    }

    /// <summary>The person is typing the first message of a chat that doesn't exist yet. Recall reads ahead on a stand-in chat, which the
    /// real one takes over when that message is sent (see <see cref="RecallThreadOf"/>).</summary>
    internal void PrefetchNewChat(string ownerId, string draft, string username)
    {
        (Thread Chat, DateTime Started) entry = draftChats.AddOrUpdate(ownerId,
            _ => (NewDraftChat(ownerId), DateTime.UtcNow),
            (_, old) => DateTime.UtcNow - old.Started < DRAFT_KEPT_FOR ? old : (NewDraftChat(ownerId), DateTime.UtcNow));
        Thread chat = entry.Chat;
        if (chat.FindChild(RECALL_CODE)?.Prefetch is null)
        {
            // The stand-in's one message is the draft, so Recall knows who is speaking.
            chat.History.Clear();
            chat.History.Add(new Prompt { AuthorName = username, Text = draft, Timestamp = DateTime.Now });
        }
        PrefetchDraft(chat, draft, username);
    }

    private static Thread NewDraftChat(string ownerId) =>
        new(ThreadPipeline.Dialogue, $"draft-{ownerId}-{Guid.NewGuid():N}") { Internal = true, HasMemoryAccess = true, OwnerId = ownerId };

    private static bool BringsNewWords(string draft, string? previous)
    {
        if (previous is null) return true;
        HashSet<string> Words(string text) => System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}']+")
            .Where(w => w.Length >= DRAFT_WORD_MIN_LENGTH).ToHashSet();
        return Words(draft).Except(Words(previous)).Any();
    }

    /// <summary>One read-ahead run of Recall on its own thread, behind any live reply on its server. What it opens stays on the thread, so
    /// the next live run finds those notes already open; it writes no memory block.</summary>
    private void StartReadAhead(Thread chat, Thread recall, CustomAgent walker, string username, string since, string searchText, string task, bool advancesChat)
    {
        CancellationTokenSource cts = new();
        recall.Prefetch = cts;
        recall.PrefetchRun = Task.Run(async () =>
        {
            Stopwatch sw = Stopwatch.StartNew();
            bool locked = false, stopped = false;
            try
            {
                await recall.RunLock.WaitAsync(cts.Token);
                locked = true;
                PrivacyMode privacy = chat.IsGuarded ? PrivacyMode.Guarded : PrivacyMode.Unrestricted;
                StartingPoints start = new MemoryApi(memory!, chat, context?.GetContext(chat.Key), privacy).Start(searchText, recall);
                if (start.Text is null) return;
                Dictionary<string, string> filled = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["audience"] = start.Audience, ["since"] = since, ["starting"] = start.Text, ["task"] = task, ["message"] = searchText,
                };
                string userText = WithTask(walker, PipelineGraph.Fill(walker.MessagePrompt ?? "", filled), task);
                int checkpoint = recall.History.Count;
                using IDisposable _ = CustomAgent.Resolve(PipelineGraph.Fill(walker.AgentPrompt, filled));
                try { await walker.Prompt(recall, userText, new PromptOptions { Username = username, Ct = cts.Token, Priority = InferencePriority.Background }); }
                catch
                {
                    if (recall.History.Count > checkpoint) recall.History.RemoveRange(checkpoint, recall.History.Count - checkpoint);
                    throw;
                }
                if (advancesChat) recall.LastChatCount = chat.GetChatHistory().Count;
                Shared.Logger.LogInformation("[Recall] ({Thread}) read ahead in {Sec:F1}s.", recall.Key, sw.Elapsed.TotalSeconds);
            }
            catch (OperationCanceledException)
            {
                stopped = true;
                Shared.Logger.LogInformation("[Recall] ({Thread}) read-ahead stopped after {Sec:F1}s: a live message needed the thread.", recall.Key, sw.Elapsed.TotalSeconds);
            }
            catch (Exception ex) { Shared.Logger.LogWarning(ex, "[Recall] ({Thread}) read-ahead failed: {Error}", recall.Key, ex.Message); }
            finally
            {
                if (locked) recall.RunLock.Release();
                recall.Prefetch = null;
                cts.Dispose();
            }
            if (!stopped && recall.PendingDraft is { } next)
            {
                recall.PendingDraft = null;
                PrefetchDraft(chat, next, username);
            }
        });
    }

    /// <summary>Reads Recall's opening (its prompt and tools, and ARI's and the speaker's notes when those are preloaded) into its slot at
    /// startup, so the first conversation doesn't spend seconds on it.</summary>
    internal async Task WarmRecallAsync(string username)
    {
        if (memory is null || !customAgents.TryGetValue(SystemAgents.Walker, out CustomAgent? walker)) return;
        Thread chat = new(ThreadPipeline.Dialogue, "warmup-recall") { Internal = true, HasMemoryAccess = true };
        chat.History.Add(new Prompt { AuthorName = username, Text = ".", Timestamp = DateTime.Now });
        Thread recall = RecallThreadOf(chat, RECALL_CODE, walker, username);
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
            Internal = true, ReadOnlyTools = true, KeepToolHistory = true, Parent = parent, Code = code, OwnerAgent = agent.Name, Label = $"{agent.Name} ({code})",
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

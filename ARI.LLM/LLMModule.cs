using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using ARI.BrainVault;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>An event broadcast to all connected SSE clients over /api/events.</summary>
/// <param name="Type">newThread | streaming | streamingFinished | threadDeleted | threadUpdated</param>
/// <param name="ThreadKey">The thread this event relates to.</param>
/// <param name="Text">Accumulated streaming text — only present for "streaming" events.</param>
public record AppEvent(string Type, string ThreadKey, string? Text = null);

/// <summary>The current processing phase of a thread, sent to watching clients via the watch SSE stream.</summary>
public enum ThreadPhase { Idle, Prefilling, Thinking, Typing, Researching, Generating }

public class LLMModule : ILLMModule, IDisposable
{
    //pipelines
    private readonly DialoguePipeline? dialoguePipeline;
    private readonly CodePipeline?     codePipeline;
    private readonly SpeechPipeline?   speechPipeline;
    
    //agents
    private readonly TextingAgent?     textingAgent;
    private readonly TalkingAgent?     talkingAgent;
    private readonly Coder?            codeArchitect;
    private readonly Memory?           memory;
    private readonly Context?          context;
    private readonly Engram?           engram;
    private readonly Refactor?         refactor;
    private readonly Awareness?        awareness;
    
    
    //dreaming
    private readonly Dreamer?           dreamer;
    private readonly DreamPipeline?     dreamPipeline;
    private readonly DreamOrchestrator? dreamOrchestrator;

    private readonly CommandService    commands;
    // One queue per physical server — two servers can genuinely run at the same time; agents sharing
    // one server share its queue and take turns. Never acquire more than one queue at once per prompt,
    // and never hold a queue across a call that prompts the same server again (see LLMQueue's own doc).
    private readonly Dictionary<Server, LLMQueue>                            queues             = new();
    private readonly LLMQueue                                                unboundQueue       = new();   // agents with no resolved server (shouldn't normally happen)
    private LLMQueue QueueFor(Server? server) => server is not null && queues.TryGetValue(server, out LLMQueue? q) ? q : unboundQueue;
    private readonly ConcurrentDictionary<string, CancellationTokenSource>  processingThreads  = new();
    private readonly ConcurrentDictionary<string, LiveCallInfo>            liveCalls           = new();
    private readonly ConcurrentDictionary<Guid, Channel<AppEvent>>         globalSubscribers   = new();
    private readonly Dictionary<string, Agent>                             agentMap            = new();
    private readonly ConcurrentDictionary<string, ThreadPipeline>          forcedPipelines     = new();
    private readonly ConcurrentDictionary<string, ThreadPhase>             threadPhases        = new();
    // Live count of /watch connections per thread — a client only holds one open while that thread is
    // its active view (see ARI.UI's attachWatch), so a count of zero means nobody is currently looking.
    // Backs the "push if unwatched" rule: a completed response only rings the owner's phone when this is 0.
    private readonly ConcurrentDictionary<string, int>                     threadWatchers      = new();
    private readonly ConcurrentDictionary<string, Thread>                                         threads           = new();

    private readonly List<Server>  _servers    = new();
    private IReadOnlyList<Model>   _allModels  = Array.Empty<Model>();
    private string                    _modelsPath = "";
    private ILogger                   _logger;

    /// <summary>All managed llama servers.</summary>
    public IReadOnlyList<Server> Servers    => _servers;
    public string                   ModelsPath => _modelsPath;

    /// <summary>All active agents, keyed by name. Navigate here to access threads and their data.</summary>
    public IReadOnlyDictionary<string, Agent> Agents => agentMap;

    /// <summary>Every thread across all pipelines, keyed by thread key.</summary>
    public IReadOnlyDictionary<string, Thread> Threads => threads;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>Case-insensitive property lookup so the agent loader tolerates either PascalCase
    /// (Name/Enabled, as persisted) or the camelCase of the [JsonPropertyName] attributes —
    /// matching the case-insensitive behaviour of <see cref="JsonOptions"/> used for deserialization.</summary>
    private static bool TryGetPropCI(JsonElement el, string name, out JsonElement value)
    {
        if (el.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty p in el.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }

        value = default;
        return false;
    }

    public LLMModule(IReadOnlyList<Server> servers, string agentsJsonPath, BrainConfig? brainConfig = null, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory is not null
            ? loggerFactory.CreateLogger("ARI.LLM")
            : Shared.Logger;

        if (loggerFactory is not null)
        {
            Shared.InitialiseLogger(loggerFactory, "ARI.LLM");
            ILogger serverLogger = loggerFactory.CreateLogger("ARI.LLM");
            foreach (Server s in servers)
                s.SetLogger(serverLogger);
        }

        _servers.AddRange(servers);
        foreach (Server s in _servers) queues[s] = new LLMQueue();

        CleanScratchpads();

        Dictionary<string, Server> serverByName = servers.ToDictionary(s => s.Name, s => s);

        Dictionary<string, JsonElement> rawAgents = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(agentsJsonPath))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(agentsJsonPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

            Dictionary<string, string>? sharedMemory = null, sharedToolSystem = null;
            if (TryGetPropCI(doc.RootElement, "Shared", out JsonElement sharedEl))
            {
                if (TryGetPropCI(sharedEl, "MemoryAgent", out JsonElement memEl))
                    sharedMemory = JsonSerializer.Deserialize<Dictionary<string, string>>(memEl.GetRawText(), JsonOptions);
                if (TryGetPropCI(sharedEl, "ToolSystem", out JsonElement toolEl))
                    sharedToolSystem = JsonSerializer.Deserialize<Dictionary<string, string>>(toolEl.GetRawText(), JsonOptions);
            }
            SharedPrompts.Load(sharedMemory, sharedToolSystem);
            // Unlike Agents.json, ToolGroups.json has no control-panel edit UI yet — read straight from
            // the shipped copy (Paths.BuildPath) rather than agentsJsonPath's seeded-into-AppData copy,
            // which only exists for files a user is meant to tune in place.
            ToolGroups.Load(Path.Combine(Paths.BuildPath, "ToolGroups.json"));

            if (TryGetPropCI(doc.RootElement, "Agents", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in arr.EnumerateArray())
                    if (TryGetPropCI(el, "name", out JsonElement nameEl) && nameEl.GetString() is string name)
                        if (TryGetPropCI(el, "enabled", out JsonElement en) && en.GetBoolean())
                            rawAgents[name] = el.Clone();   // Clone: JsonElements must outlive the using-disposed JsonDocument
        }

        T Deserialize<T>(JsonElement el) where T : Agent
        {
            T agent = JsonSerializer.Deserialize<T>(el.GetRawText(), JsonOptions)!;

            if (serverByName.TryGetValue(agent.ServerName, out Server? bound))
            {
                agent.Endpoint    = bound.FullEndpoint;
                agent.Server = bound;
            }
            else if (_servers.Count > 0)
            {
                // No binding, or one naming a server that no longer exists. Bindings are machine facts,
                // so the shipped Agents.json carries none — on a fresh install this is how every agent
                // finds the demo server. Lowest-index server, lowest-index slot, unless the user said
                // otherwise. Logged because "why is this agent on that server" should be answerable
                // from the log rather than by reading this method.
                Server first = _servers[0];
                _logger.LogInformation(
                    agent.ServerName.Length == 0
                        ? "Agent '{Agent}' has no server binding — defaulting to '{Server}' slot 0."
                        : "Agent '{Agent}' is bound to unknown server '{Missing}' — defaulting to '{Server}' slot 0.",
                    agent.Name, agent.ServerName.Length == 0 ? first.Name : agent.ServerName, first.Name);

                agent.ServerName  = first.Name;
                agent.Endpoint    = first.FullEndpoint;
                agent.Server = first;
            }
            else
            {
                _logger.LogError("Agent '{Agent}' cannot be bound — no servers are configured.", agent.Name);
            }

            if (agent.Server is not null)
            {
                if (agent.SlotName is { Length: > 0 })
                {
                    agent.Slot = agent.Server.Slots.FirstOrDefault(sl => sl.Name.Equals(agent.SlotName, StringComparison.OrdinalIgnoreCase));
                    if (agent.Slot is null)
                        _logger.LogWarning("Agent '{Agent}' names slot '{Slot}' which doesn't exist on server '{Server}' — falling back to its first slot.",
                            agent.Name, agent.SlotName, agent.Server.Name);
                }
                // No name given (or it didn't resolve) — default to the server's first slot, same as the
                // old raw agent.Slot ??= 0 behaviour, so an agent still gets pinned/context-derived out of
                // the box without needing the control panel touched first.
                if (agent.Slot is null && agent.Server.Slots.Count > 0)
                {
                    agent.Slot = agent.Server.Slots[0];
                    agent.SlotName  = agent.Slot.Name;
                }
            }
            agent.Queue = QueueFor(agent.Server);
            return agent;
        }

        if (brainConfig is not null)
        {
            IndexStats brainStats = Brain.Initialize(brainConfig);
            _logger.LogInformation("Brain vault indexed: {Notes} notes, {Edges} edges, {Aliases} aliases, {Thoughts} thoughts.",
                brainStats.Notes, brainStats.Edges, brainStats.Aliases, brainStats.Thoughts);
            if (brainStats.SkippedNotes.Count > 0)
            {
                _logger.LogWarning("Brain vault: {Count} note(s) skipped on index due to duplicate titles — reconcile these in the vault:", brainStats.SkippedNotes.Count);
                foreach (string skipped in brainStats.SkippedNotes)
                    _logger.LogWarning("  [Brain] skipped note: {Detail}", skipped);
            }
        }

        if (rawAgents.TryGetValue("Context", out JsonElement contextEl))
        {
            context = Deserialize<Context>(contextEl);
            int memoryLimit = rawAgents.TryGetValue("Dialogue", out JsonElement dlgEl)
                ? JsonSerializer.Deserialize<Dialogue>(dlgEl.GetRawText(), JsonOptions)!.ShortTermMemoryLimit ?? 25
                : 25;
            context.Init(memoryLimit);
            // Each context update yields a fresh short title — rename the thread and notify the UI.
            context.TitleUpdated = (key, title) =>
            {
                if (threads.TryGetValue(key, out Thread? t) && t.Title != title)
                {
                    t.Title = title;
                    Broadcast(new AppEvent("threadUpdated", key));
                }
            };
            _logger.LogInformation("Context tracker is active.");
        }

        if (rawAgents.TryGetValue("Dialogue", out JsonElement dialogueEl))
        {
            textingAgent = Deserialize<TextingAgent>(dialogueEl);
            talkingAgent = Deserialize<TalkingAgent>(dialogueEl);
            agentMap["Dialogue"] = textingAgent;
        }

        if (rawAgents.TryGetValue("Coder", out JsonElement architectEl))
        {
            codeArchitect = Deserialize<Coder>(architectEl);
            agentMap["Coder"] = codeArchitect;
            _logger.LogInformation("Coder agent is active. MaxContext: {Ctx} tokens.", codeArchitect.BudgetContext);
        }

        // Speech conversational-awareness gate. Uses its own Agents.json entry if present, otherwise
        // gets the same server/slot-fallback treatment as any other unbound agent (Deserialize on an
        // empty definition), so it still works out of the box with no system prompt.
        if (rawAgents.TryGetValue("Awareness", out JsonElement awarenessEl))
        {
            awareness = Deserialize<Awareness>(awarenessEl);
            _logger.LogInformation("Awareness is active.");
        }
        else if (_servers.Count > 0)
        {
            awareness = Deserialize<Awareness>(JsonDocument.Parse("{\"name\":\"Awareness\"}").RootElement);
            _logger.LogWarning("No Awareness entry in Agents.json — using default server/slot with no system prompt. Add an Awareness entry to configure it.");
        }

        if (Brain.Ready && rawAgents.TryGetValue("Memory", out JsonElement memoryEl))
        {
            Memory mem = Deserialize<Memory>(memoryEl);
            if (mem.HopLimit > 0)
            {
                memory = mem;
                agentMap["Memory"] = memory;
                _logger.LogInformation("Memory agent is active. Hop limit: {HopLimit}.", memory.HopLimit);
            }
        }

        if (Brain.Ready && textingAgent is not null)
        {
            if (rawAgents.TryGetValue("Engram", out JsonElement engramEl))
            {
                engram = Deserialize<Engram>(engramEl);
                engram.PersistentDir = PersistentDataDir;
                engram.Registry = threads;
                engram.Notify = NotifyWatchers;
                engram.Init(textingAgent, context, threads);
                engram.SweepCompleted += key => NotifyWatchers(key);
                agentMap["Engram"] = engram;
                _logger.LogInformation("Engram is active. Brain connected.");
            }

            if (rawAgents.TryGetValue("Refactor", out JsonElement refactorEl))
            {
                refactor = Deserialize<Refactor>(refactorEl);
                refactor.engram = engram;
                refactor.PersistentDir = PersistentDataDir;
                refactor.Registry = threads;
                refactor.Notify = NotifyWatchers;
                agentMap["Refactor"] = refactor;
                _logger.LogInformation("Refactor is active.");
            }

            commands = new CommandService(engram, refactor);
        }
        else
        {
            commands = new CommandService(engram);
        }

        if (textingAgent is not null && talkingAgent is not null)
        {
            dialoguePipeline = new DialoguePipeline(textingAgent, memory, context, engram, processingThreads, liveCalls, NotifyWatchers);
            dialoguePipeline.ThreadBufferFull     += key => NotifyWatchers(key);
            dialoguePipeline.ThreadBecameInactive += key => NotifyWatchers(key);

            speechPipeline = new SpeechPipeline(talkingAgent, memory, context, engram, processingThreads, liveCalls, NotifyWatchers);
            speechPipeline.ThreadBufferFull     += key => NotifyWatchers(key);
            speechPipeline.ThreadBecameInactive += key => NotifyWatchers(key);
        }

        if (codeArchitect is not null)
            codePipeline = new CodePipeline(codeArchitect, processingThreads, liveCalls, NotifyWatchers);

        if (textingAgent is not null)
        {
            dreamer = new Dreamer
            {
                Name         = "Dreamer",
                Priority     = (int)InferencePriority.Dream,   // background: any real prompt queues ahead of it
                ServerName   = textingAgent.ServerName,
                Endpoint     = textingAgent.Endpoint,
                Server       = textingAgent.Server,
                SlotName               = textingAgent.SlotName,
                Think                  = textingAgent.Think,
                ReasoningEffortOverride = "xhigh",
                UsePersona             = true,
                AgentPrompt =
                    "You are in a dream state. No user is present and no one is waiting — this is unstructured time " +
                    "for you to think, explore, and reflect as deeply as you want. There is no time pressure. " +
                    "Call only one tool at a time — never make parallel calls.\n\n" +
                    "What you can do:\n" +
                    "- Recall your memories (facts, people, past conversations): search_brain to find notes by title/content (returns title — path), then recall_memory to read the full note\n" +
                    "- Browse your projects: list_projects, then bind_project — filesystem tools (read_file, list_directory, search_files, etc.) unlock in the same step\n" +
                    "- Switch projects freely: call bind_project again with a different id\n" +
                    "- Search the web: search_web, fetch_page\n" +
                    "- Search an Obsidian note vault: search_vault — only useful if the bound project is an Obsidian graph, not a code repo; calling it on code will return nothing\n" +
                    "- Check the current time: get_time — call this before waking so you can judge whether now is a reasonable time to send a message\n\n" +
                    "The wake tool ends the dream and sends your owner a message that will notify them. " +
                    "The threshold is 'worth a notification' — not urgency. A question you need answered, a curiosity, something you noticed, something you want to say — all of these clear the bar. " +
                    "Call get_time first and use your judgement about whether it's a reasonable time to interrupt. " +
                    "Write the message in your own voice, as yourself, informed by everything you found. " +
                    "Always fill in context as a private briefing to your waking self: include the relevant notes, what you were trying to figure out, what state they seem to be in, and what you're hoping to do once they respond.",
            };
            // Built manually rather than through Deserialize<T>, so it needs its queue assigned by hand —
            // without this, Agent.Prompt's per-step acquisition is a silent no-op (Queue is null) and
            // Dreamer's turns get zero coordination with anything else on the same server.
            dreamer.Queue = QueueFor(dreamer.Server);
            dreamPipeline = new DreamPipeline(
                dreamer,
                onWake: (content, context, title) =>
                    CreateProactiveDialogueThread(content,
                        title: string.IsNullOrWhiteSpace(title) ? "Wake" : title,
                        dreamContext: context),
                processingThreads, liveCalls, NotifyWatchers);
            dreamOrchestrator = new DreamOrchestrator(
                dreamPipeline,
                dreamer,
                QueueFor(dreamer.Server),
                isDreamingEnabled: () => (Modules.Scheduler?.DreamingEnabled ?? false) && !ConversationActive,
                createDreamThread: () =>
                {
                    string key = $"dream-{DateTime.Now:yyyyMMdd-HHmmss}";
                    Thread t = new Thread(ThreadPipeline.Dream, key) { Internal = true };
                    threads[key] = t;
                    return t;
                },
                destroyDreamThread: t => t.Delete());
        }

        if (dreamer is not null)
            agentMap["Dreamer"] = dreamer;

        // Wire phase tracking on every agent so watch clients know which phase is active.
        foreach (Agent agent in agentMap.Values)
        {
            agent.OnPhaseChange = (threadKey, phase) =>
            {
                Shared.Logger.LogDebug("[PhaseChange] {Agent} ({Thread}) → {Phase}", agent.Name, threadKey, phase);
                if (phase == ThreadPhase.Idle) threadPhases.TryRemove(threadKey, out _);
                else                           threadPhases[threadKey] = phase;
                NotifyWatchers(threadKey);
            };
        }
    }

    // ── Thread registry ──────────────────────────────────────────────────────────

    private Thread GetOrCreateThread(ThreadPipeline type, string threadKey, string? platformContext = null)
    {
        if (threads.TryGetValue(threadKey, out Thread? existing)) return existing;
        Thread thread = new Thread(type, threadKey, platformContext);
        threads[threadKey] = thread;
        // list_tools/request_tools are always warm (issue #126) and universal — no agent-identity gate.
        // What a group actually resolves to depends on ToolFactories reading this thread's bound context
        // (FilesystemRoot etc.), set by whichever agent runs on it (Coder.RunLoop, MemoryAgent.RegisterTools).
        // Threads created outside this choke point (MemoryAgent's internal epoch threads) register their
        // own copy for the same reason.
        new ListTools(thread).Register(thread);
        new RequestTools(thread).Register(thread);
        // Discord threads get discord_tools hot — no request_tools round-trip needed.
        bool isDiscord = threadKey.StartsWith("dm:", StringComparison.OrdinalIgnoreCase) ||
                         threadKey.StartsWith("guild:", StringComparison.OrdinalIgnoreCase);
        if (isDiscord)
            ToolFactories.LoadGroup("discord_tools", thread);
        // propose_persona_edit is hot for the same reason: the moment worth proposing a persona change is a
        // moment the user is criticising her, and a list_tools → request_tools discovery hop is not something
        // to rely on mid-apology. Chat clients only — the proposal is approved by clicking a diff, which a
        // Discord or voice thread has no way to show.
        if (!isDiscord && type is ThreadPipeline.Dialogue or ThreadPipeline.Code)
            ToolFactories.LoadGroup("persona_tools", thread);
        // Image/video generation tools are always hot when the module is ready — no request_tools hop.
        if (Modules.ImageGen?.IsReady == true && type is ThreadPipeline.Dialogue or ThreadPipeline.Speech)
            ToolFactories.LoadGroup("image_tools", thread);
        thread.Updated           += () => Broadcast(new AppEvent("threadUpdated", threadKey));
        thread.Deleted           += () => { threads.TryRemove(threadKey, out _); Broadcast(new AppEvent("threadDeleted", threadKey)); };
        thread.Streaming         += text => Broadcast(new AppEvent("streaming", threadKey, text));
        thread.StreamingFinished += () =>
        {
            Broadcast(new AppEvent("streamingFinished", threadKey));
            PushIfUnwatched(thread, threadKey);
        };
        thread.ScratchpadFileReady += url => Broadcast(new AppEvent("imageReady", threadKey, url));
        // Persist a plain-text transcript to ChatHistory after every completed exchange.
        if (type is not ThreadPipeline.Dream)
            thread.ExchangeCompleted += (_, _) => ChatHistoryLogger.Write(thread);
        // Engram (or a mark-processed no-op) fires on entry to dormant — the single gate before deletion.
        thread.BecameDormant    += () => OnThreadDormant(thread);
        if (type is ThreadPipeline.Dialogue && textingAgent is not null)
        {
            thread.Deleted        += () => textingAgent.RaiseThreadDeleted(threadKey);
            thread.BufferFull     += () => textingAgent.RaiseThreadBufferFull(threadKey);
            thread.BecameInactive += () => textingAgent.RaiseThreadBecameInactive(threadKey);
        }
        else if (type is ThreadPipeline.Speech && talkingAgent is not null)
        {
            thread.Deleted        += () => talkingAgent.RaiseThreadDeleted(threadKey);
            thread.BufferFull     += () => talkingAgent.RaiseThreadBufferFull(threadKey);
            thread.BecameInactive += () => talkingAgent.RaiseThreadBecameInactive(threadKey);
        }
        if (type is ThreadPipeline.Dialogue or ThreadPipeline.Speech)
        {
            if (context is not null)
                thread.ExchangeCompleted += (user, asst) =>
                {
                    string uname = thread.History.OfType<Prompt>().LastOrDefault()?.AuthorName ?? "User";
                    _ = context.Update(threadKey, user, asst, uname);
                };

            // Every TurnsBeforeSweep exchanges, an active thread gets an ordinary sweep without waiting to go dormant.
            if (engram is not null)
                thread.ExchangeCompleted += (_, _) =>
                {
                    if (thread.Internal || !thread.HasUserMessages || !thread.IsOwnerThread) return;
                    if (!engram.ShouldIntervalSweep(threadKey)) return;
                    _ = Task.Run(async () =>
                    {
                        try { await engram.RunEngram(threadKey, "interval"); }
                        catch (Exception ex) { _logger.LogWarning("[Interval] Engram failed for {Key}: {Err}", threadKey, ex.Message); }
                    });
                };
        }
        Broadcast(new AppEvent("newThread", threadKey));
        return thread;
    }

    /// <summary>
    /// Ensures the thread runs on the given pipeline, converting it if it currently runs on another.
    /// The thread is rebuilt as the target type (so it gets that type's event wiring) with its history
    /// and attachments carried over. A no-op when the thread already runs on that pipeline, and equivalent
    /// to GetOrCreateThread when no thread exists yet. Safe to call at any point in a thread's life.
    /// </summary>
    private Thread Recategorise(ThreadPipeline type, string threadKey, string? platformContext = null)
    {
        if (!threads.TryGetValue(threadKey, out Thread? existing))
            return GetOrCreateThread(type, threadKey, platformContext);
        if (existing.Pipeline == type)
            return existing;

        threads.TryRemove(threadKey, out _);
        Thread converted = GetOrCreateThread(type, threadKey, platformContext);
        converted.History.AddRange(existing.History);
        return converted;
    }

    // ── Agent assignment ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reassigns a live agent to a different server. Resolves the server name to an endpoint
    /// from the current server list. Returns false if the agent or server is not found.
    /// </summary>
    public bool AssignAgentServer(string agentName, string serverName)
    {
        if (!agentMap.TryGetValue(agentName, out Agent? agent)) return false;
        Server? server = _servers.FirstOrDefault(s => s.Name.Equals(serverName, StringComparison.OrdinalIgnoreCase));
        if (server is null) return false;
        agent.ServerName  = server.Name;
        agent.Endpoint    = server.FullEndpoint;
        agent.Server = server;
        // A slot name from the old server has no meaning here — re-resolve against the new one, or
        // fall back to unpinned if it doesn't have a same-named slot.
        agent.Slot = agent.SlotName is { Length: > 0 }
            ? server.Slots.FirstOrDefault(sl => sl.Name.Equals(agent.SlotName, StringComparison.OrdinalIgnoreCase))
            : null;
        return true;
    }

    /// <summary>
    /// Assigns a named slot (on the agent's currently-bound server) to a live agent. Pass null/empty
    /// to unpin. Returns false if the agent is not found; silently unpins if the name doesn't match
    /// any of the bound server's slots (same behaviour as at load time).
    /// </summary>
    public bool AssignAgentSlot(string agentName, string? slotName)
    {
        if (!agentMap.TryGetValue(agentName, out Agent? agent)) return false;
        agent.SlotName = slotName;
        agent.Slot = agent.Server is not null && slotName is { Length: > 0 }
            ? agent.Server.Slots.FirstOrDefault(sl => sl.Name.Equals(slotName, StringComparison.OrdinalIgnoreCase))
            : null;
        return true;
    }

    // ── Server lifecycle ─────────────────────────────────────────────────────────

    /// <summary>
    /// Boot all servers that have BootStartup = true, loading their assigned model.
    /// Callers supply the models lookup (from PersistentData) and the path to model files.
    /// </summary>
    public async Task StartServersAsync(IReadOnlyList<Model> allModels, string modelsPath)
    {
        _allModels  = allModels;
        _modelsPath = modelsPath;

        List<(Server Server, Model? Model)> bootList = _servers
            .Where(s => s.BootStartup)
            .Select(s => (s, s.CurrentModelName is not null
                ? allModels.FirstOrDefault(m => m.Name.Equals(s.CurrentModelName, StringComparison.OrdinalIgnoreCase))
                : null))
            .ToList();

        // Phase 1 — download gate. Ensure EVERY boot server's model files are present before ANY server
        // launches, so no server comes online (and starts serving requests) while another is still
        // downloading. Sequential: on a tight disk, one large download at a time is safer than several
        // at once. A download that genuinely fails drops that server from the launch set and is logged,
        // rather than throwing — one bad model must not take down Core or the other servers.
        List<(Server Server, Model? Model)> ready = new();
        foreach ((Server server, Model? model) in bootList)
        {
            try
            {
                await server.EnsureModelsAsync(model, modelsPath);
                ready.Add((server, model));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Server}] model download failed — this server will not start; others continue.", server.Name);
            }
        }

        // Phase 2 — launch every server whose model is now present, in parallel. A per-server guard keeps
        // one failed launch from propagating out and crashing Core; the server is left cleanly offline.
        List<Task> boots = new();
        foreach ((Server server, Model? model) in ready)
            boots.Add(BootOne(server, model));
        await Task.WhenAll(boots);

        dreamOrchestrator?.Start();

        async Task BootOne(Server server, Model? model)
        {
            try { await server.StartAsync(model, modelsPath); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Server}] failed to start — leaving it offline; other servers continue.", server.Name);
                server.Stop();
            }
        }
    }

    public Task StopAllServersAsync()
    {
        foreach (Server server in _servers)
            server.Stop();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Replace the in-memory server list after a config restore.
    /// All servers must already be stopped before calling this.
    /// </summary>
    public void ReplaceServers(IReadOnlyList<Server> servers)
    {
        _servers.Clear();
        _servers.AddRange(servers);
        queues.Clear();
        foreach (Server s in _servers) queues[s] = new LLMQueue();
    }

    public void AddServer(Server server)
    {
        _servers.Add(server);
        queues[server] = new LLMQueue();
    }

    public void RemoveServer(Guid id) => _servers.RemoveAll(s => s.Id == id);

    public void UpdateServer(Server updated)
    {
        int idx = _servers.FindIndex(s => s.Id == updated.Id);
        if (idx >= 0) _servers[idx] = updated;
    }

    public async Task RestartAllServersAsync()
    {
        await StopAllServersAsync();
        await StartServersAsync(_allModels, _modelsPath);
    }

    public void Dispose()
    {
        dreamOrchestrator?.Dispose();
        engram?.Dispose();
        foreach (Server server in _servers)
            server.Dispose();
    }

    // ── Brain backups ───────────────────────────────────────────────────────────
    public bool BrainAvailable => Brain.Ready;
    public string BackupBrain()                   => Brain.Ready ? BrainBackup.Backup()            : "Brain is not available.";
    public List<BackupInfo> ListBrainBackups()    => Brain.Ready ? BrainBackup.ListBackups()       : new List<BackupInfo>();
    public string RestoreBrainBackup(string file) => Brain.Ready ? BrainBackup.RestoreBackup(file) : "Brain is not available.";

    // ── Prompting ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Pins a thread to a specific pipeline, overriding the default routing. Honoured by <see cref="Route"/>
    /// on every send — including threads that already have history — so re-pinning flips the thread to
    /// the new pipeline on the next message (carrying history via <see cref="Recategorise"/>). Used both
    /// for explicit selection at thread creation and for switching a live thread from the UI.
    /// </summary>
    public void ForcePipeline(string threadKey, ThreadPipeline pipeline)
    {
        forcedPipelines[threadKey] = pipeline;
        GetOrCreateThread(pipeline, threadKey);
    }

    /// <summary>Pre-marks a thread to run through the Code pipeline. Thin wrapper over <see cref="ForcePipeline"/>.</summary>
    public void ForceCodeThread(string threadKey) => ForcePipeline(threadKey, ThreadPipeline.Code);

    /// <summary>Binds a ServerFs project onto a thread immediately — called synchronously by bind_project
    /// so the model can use filesystem_tools THIS turn instead of waiting for the next message. No-op for
    /// RemoteFs projects — their files aren't on this server's disk.</summary>
    public void BindProjectContext(string threadKey, string? rootPath, bool isVault)
    {
        if (!Threads.TryGetValue(threadKey, out Thread? thread) || rootPath is null) return;

        // The brain vault is accessed only through memory_tools — never via the generic filesystem.
        // If a project somehow points at the vault root, silently no-op the bind so write_file / edit_file
        // never get registered over it on a non-memory thread.
        if (Brain.Ready &&
            string.Equals(Path.GetFullPath(rootPath), Path.GetFullPath(Brain.VaultRoot),
                          StringComparison.OrdinalIgnoreCase))
            return;

        thread.FilesystemRoot  = rootPath;
        thread.IsBrainVault = isVault;
        thread.Ct           = CancellationToken.None;
    }

    /// <summary>Engram gate on entry to dormant — the ONLY point at which a thread earns the right to be
    /// deleted. Runs a sweep for Dialogue/Speech threads that carry user messages and aren't already
    /// processed; otherwise (Code thread, or an unanswered proactive with nothing to learn) marks the
    /// thread processed so its deletion timer may proceed. RunEngram sets EngramProcessed on success; if
    /// it can't run (disabled / a concurrent sweep holds the lock) the flag stays false and the thread's
    /// delete-retry poll tries again.</summary>
    private void OnThreadDormant(Thread thread)
    {
        bool canSweep = engram is not null && !thread.Internal && thread.HasUserMessages && !thread.EngramProcessed && thread.IsOwnerThread;
        if (!canSweep) { thread.EngramProcessed = true; return; }

        if (thread.Pipeline is ThreadPipeline.Dialogue or ThreadPipeline.Speech)
        {
            _ = Task.Run(async () =>
            {
                try { await engram!.RunEngram(thread.Key, "dormant"); }
                catch (Exception ex) { _logger.LogWarning("[Dormant] Engram failed for {Key}: {Err}", thread.Key, ex.Message); }
            });
        }
        else if (thread.Pipeline is ThreadPipeline.Code)
        {
            _ = Task.Run(async () =>
            {
                try { await engram!.RunCodeSummary(thread.Key, "dormant"); }
                catch (Exception ex) { _logger.LogWarning("[Dormant] Code summary failed for {Key}: {Err}", thread.Key, ex.Message); }
            });
        }
        else
        {
            thread.EngramProcessed = true;
        }
    }

    /// <summary>Manually close a thread (close-thread button): remove it from the UI immediately, run a real
    /// Engram sweep (forced past any disabled gate — a user closing wants it saved), then delete once the
    /// sweep finishes. The thread stays in the registry, hidden, until Engram completes.</summary>
    public async Task<bool> CloseThreadAsync(string threadKey)
    {
        if (!threads.TryGetValue(threadKey, out Thread? thread)) return false;

        Cancel(threadKey);   // stop any active send before we sweep + delete

        // 1) Clear it from the UI now; the thread lingers in the registry until Engram is done.
        Broadcast(new AppEvent("threadDeleted", threadKey));

        // 2) Guarantee the conversation is saved before deletion (honours the no-delete-before-Engram rule).
        if (engram is not null && !thread.Internal && thread.HasUserMessages)
        {
            if (thread.Pipeline is ThreadPipeline.Dialogue or ThreadPipeline.Speech)
            {
                try { await engram.RunEngram(threadKey, "closed", force: true); }
                catch (Exception ex) { _logger.LogWarning("[Close] Engram failed for {Key}: {Err}", threadKey, ex.Message); }
            }
            else if (thread.Pipeline is ThreadPipeline.Code)
            {
                try { await engram.RunCodeSummary(threadKey, "closed"); }
                catch (Exception ex) { _logger.LogWarning("[Close] Code summary failed for {Key}: {Err}", threadKey, ex.Message); }
            }
        }

        // 3) Delete for real (fires Deleted → registry removal + a final threadDeleted broadcast).
        thread.Delete();
        return true;
    }

    /// <summary>Whether a conversational-awareness gate is available.</summary>
    public bool AwarenessAvailable => awareness is not null;

    /// <summary>
    /// Fast gate for the Speech pipeline: is this transcript addressed to Ari, or background talk?
    /// Returns true (addressed) when no gate is configured or on error, so nothing is silently dropped.
    /// </summary>
    public async Task<bool> EvaluateAwareness(string transcript, string? context = null, CancellationToken ct = default)
    {
        if (awareness is null || string.IsNullOrWhiteSpace(transcript)) return true;
        try
        {
            // No outer acquire — awareness.IsAddressed -> Agent.Prompt already acquires this agent's
            // queue itself, per step. An outer acquire here would deadlock the same way Route's did.
            return await awareness.IsAddressed(transcript, context, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "[Awareness] evaluation failed; assuming addressed."); return true; }
    }

    /// <summary>
    /// Fast gate for text chat: given recent thread history and a new message, should Ari respond?
    /// Returns true when no gate is configured or on error.
    /// </summary>
    public async Task<bool> EvaluateTextAwareness(string threadKey, string latestMessage, CancellationToken ct = default)
    {
        if (awareness is null || string.IsNullOrWhiteSpace(latestMessage)) return true;
        try
        {
            List<ThreadMessage> recent = threads.TryGetValue(threadKey, out Thread? thread)
                ? thread.GetChatHistory(maxMessages: 10)
                : [];
            // No outer acquire — same reasoning as EvaluateAwareness above.
            return await awareness.ShouldRespond(recent, latestMessage, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "[Awareness] text evaluation failed; assuming addressed."); return true; }
    }

    public Task<string> Prompt(string threadKey, string prompt, string username, string? platformContext = null, List<Attachment>? messageAttachments = null, InferencePriority priority = InferencePriority.Normal)
        => Route(threadKey, prompt, username, platformContext, null, CancellationToken.None, messageAttachments, priority: priority);

    public Task<string> PromptStreaming(string threadKey, string prompt, string username, string? platformContext, Func<string, Task> onDelta, CancellationToken ct = default, List<Attachment>? messageAttachments = null, string? localPath = null, InferencePriority priority = InferencePriority.Normal, SpeechSteeringContext? steering = null, Func<string, Task>? onTextDelta = null)
        => Route(threadKey, prompt, username, platformContext, onDelta, ct, messageAttachments, localPath, priority, steering, onTextDelta);

    private async Task<string> Route(string threadKey, string prompt, string username, string? platformContext, Func<string, Task>? onDelta, CancellationToken externalCt, List<Attachment>? messageAttachments = null, string? localPath = null, InferencePriority priority = InferencePriority.Normal, SpeechSteeringContext? steering = null, Func<string, Task>? onTextDelta = null)
    {
        if (textingAgent is null)
            throw new ModelNotFoundException("Dialogue model is not loaded or is not enabled.");

        // New prompt arrived mid-processing — cancel the previous one
        if (IsThreadProcessing(threadKey))
            Interrupt(threadKey);

        // Cancel any in-flight dream immediately so it doesn't compete with this live prompt.
        dreamOrchestrator?.NotifyUserActivity();

        // No outer acquire here — this pipeline calls into Memory's own Prompt (a separate agent,
        // separate per-step acquisition) before the primary agent's own Prompt call. Holding a queue
        // slot across both would deadlock the moment either of them tried to acquire it themselves.
        // Each agent's own Agent.Prompt loop acquires its bound server's queue per step; that's the
        // only place a slot is ever held.

        // create a cancellation token in case this prompt needs cancelling later
        CancellationTokenSource cts = externalCt.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(externalCt)
            : new CancellationTokenSource();
        processingThreads[threadKey] = cts;
        NotifyWatchers(threadKey);   // push "prefilling" snapshot immediately so the watch client doesn't wait for the first delta

        // Discord threads always use Dialogue.
        bool isDiscordThread = threadKey.StartsWith("dm:", StringComparison.OrdinalIgnoreCase)
                            || threadKey.StartsWith("guild:", StringComparison.OrdinalIgnoreCase);
        if (isDiscordThread)
        {
            Thread dlgThread = GetOrCreateThread(ThreadPipeline.Dialogue, threadKey, platformContext);
            return await dialoguePipeline!.ExecuteAsync(dlgThread, threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments);
        }

        // No more classifier. Routing is deterministic: an explicit pin (UI selection, or a bound
        // Repository project — see ChatController.ForceCodeThread) wins outright; a thread that
        // already has history keeps whatever pipeline it's already on; anything else is Dialogue —
        // she's multi-purpose by default (list_tools/request_tools cover ad hoc code work), and only
        // a genuinely bound Repository project switches the pipeline, same as switching from Claude
        // to Claude Code.
        threads.TryGetValue(threadKey, out Thread? existing);
        string? agent = existing?.Pipeline.ToString();

        if (forcedPipelines.TryGetValue(threadKey, out ThreadPipeline forced))
        {
            agent = forced.ToString();
            _logger.LogInformation($"[Router] ({threadKey}) → {agent} (forced)");
        }
        else if (agent is null)
        {
            agent = "Dialogue";
        }

        switch (agent)
        {
            case "Code":
            {
                Thread codeThread = Recategorise(ThreadPipeline.Code, threadKey, platformContext);
                return await (codePipeline ?? (Pipeline)dialoguePipeline!).ExecuteAsync(codeThread, threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments, localPath);
            }
            case "Speech":
            {
                Thread speechThread = Recategorise(ThreadPipeline.Speech, threadKey, platformContext);
                speechPipeline?.SetSteering(threadKey, steering);
                return await (speechPipeline ?? (Pipeline)dialoguePipeline!).ExecuteAsync(speechThread, threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments, localPath, onTextDelta);
            }
            default:
            {
                Thread dlgThread = Recategorise(ThreadPipeline.Dialogue, threadKey, platformContext);
                return await dialoguePipeline!.ExecuteAsync(dlgThread, threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments);
            }
        }
    }

    // ── Commands ────────────────────────────────────────────────────────────────

    public async Task<string?> HandleCommand(string? threadKey, string input)
    {
        // Show the input straight away to acknowledge the command, then run it.
        if (threadKey is not null && threads.TryGetValue(threadKey, out Thread? cmdThreadPre))
            cmdThreadPre.AddItem(new CommandInput { Input = input, Timestamp = DateTime.Now });

        string trimmed = input.Trim().ToLowerInvariant();

        string? result;
        if (trimmed == "/code" || trimmed == "/uncode")
        {
            // handle thread migration to new agent
            result = threadKey is null      ? "No active thread."
                   : trimmed == "/code"     ? "Switched to **Code** mode."
                   :                          "Switched to **Dialogue** mode.";
        }
        else
        {
            result = await commands.Handle(input, threadKey);
        }

        if (threadKey is not null && threads.TryGetValue(threadKey, out Thread? cmdThread))
        {
            if (result is not null) cmdThread.AddItem(new CommandResponse { Response = result, Timestamp = DateTime.Now });
            else                    cmdThread.DropLastCommandInput();
        }
        return result;
    }

    // ── Global event bus ────────────────────────────────────────────────────────

    private void Broadcast(AppEvent evt)
    {
        foreach (Channel<AppEvent> ch in globalSubscribers.Values)
            ch.Writer.TryWrite(evt);
    }

    public void BroadcastTaskState(string taskName, bool running)
        => Broadcast(new AppEvent(running ? "taskStarted" : "taskStopped", "", taskName));

    public void BroadcastProjectsChanged()
        => Broadcast(new AppEvent("projectsChanged", ""));

    /// <summary>Subscribe to the global event stream. Dispose the returned handle to unsubscribe.</summary>
    public IDisposable Subscribe(Channel<AppEvent> channel)
    {
        Guid id = Guid.NewGuid();
        globalSubscribers[id] = channel;
        return new SubscriberHandle(globalSubscribers, id, channel);
    }

    // Kept for the per-thread debug-panel watch endpoint — translates global events into a bool? signal.
    private void NotifyWatchers(string threadKey) => Broadcast(new AppEvent("threadUpdated", threadKey));

    private sealed class SubscriberHandle(
        ConcurrentDictionary<Guid, Channel<AppEvent>> registry,
        Guid id, Channel<AppEvent> channel) : IDisposable
    {
        public void Dispose()
        {
            registry.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    // ── WEB INTEGRATION ─────────────────────────────────────────────────────────

    /// <summary>
    /// Real-time snapshot of every LLM call currently streaming across all agents.
    /// Used by the control panel to show a live token counter while a response is generating.
    /// </summary>
    public IReadOnlyList<LiveCallInfo> LiveCalls()
    {
        List<LiveCallInfo> result = new(liveCalls.Values);
        result.AddRange(threads.Values.Where(t => t.Internal).Select(t => t.LiveCall).Where(l => l is not null)!);
        return result;
    }

    /// <summary>
    /// Historical log of every completed LLM call across all agents, ordered by time.
    /// Used by the control panel to render the token usage graph.
    /// </summary>
    public IReadOnlyList<LlmCallStat> CallStats()
    {
        List<LlmCallStat> result = new();

        foreach (KeyValuePair<string, Thread> entry in threads)
            foreach (ThreadItem item in entry.Value.History)
            {
                if (item is Response resp && (resp.Data.CompletionTokens > 0 || resp.Data.PromptTokens > 0))
                    result.Add(new LlmCallStat(entry.Value.Pipeline.ToString(), entry.Key, resp.Timestamp,
                        resp.Data.CompletionTokens, resp.Data.OutputTokenLimit,
                        resp.Data.PromptTokens, resp.Data.ContextTokenLimit,
                        resp.Data.HadImageAttachments, resp.Data.EstimatedTextPromptTokens,
                        resp.Data.ImageTokenLimit));
            }

        result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return result;
    }

    public IDisposable WatchThread(string threadKey, Channel<bool?> channel)
    {
        threadWatchers.AddOrUpdate(threadKey, 1, (_, n) => n + 1);
        int released = 0;
        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            threadWatchers.AddOrUpdate(threadKey, 0, (_, n) => Math.Max(0, n - 1));
        }

        Channel<AppEvent> appCh = Channel.CreateUnbounded<AppEvent>(new UnboundedChannelOptions { SingleReader = true });
        IDisposable handle = Subscribe(appCh);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (AppEvent evt in appCh.Reader.ReadAllAsync())
                {
                    if (evt.ThreadKey != threadKey) continue;
                    channel.Writer.TryWrite(evt.Type == "threadDeleted" ? null : true);
                    if (evt.Type == "threadDeleted") break;
                }
            }
            catch { /* channel completed */ }
            channel.Writer.TryComplete();
            Release();
        });
        return new ActionOnDispose(() => { handle.Dispose(); Release(); });
    }

    /// <summary>Whether at least one client currently has <paramref name="threadKey"/> open as its active
    /// view. Used to decide whether a completed response needs a push notification instead.</summary>
    public bool HasWatchers(string threadKey) => threadWatchers.TryGetValue(threadKey, out int n) && n > 0;

    private sealed class ActionOnDispose(Action onDispose) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) onDispose();
        }
    }

    public bool IsThreadProcessing(string threadKey) => processingThreads.ContainsKey(threadKey);

    /// <summary>Tells watchers a thread's rendered history changed for a reason outside a turn — currently
    /// a persona proposal being approved or rejected, which repaints its card without the model running.</summary>
    public void NotifyThreadUpdated(string threadKey)
    {
        if (threads.TryGetValue(threadKey, out Thread? thread)) thread.RaiseUpdated();
    }

    public bool IsEngramSweeping(string threadKey) => engram?.IsSweeping(threadKey) ?? false;

    /// <summary>How many completed exchanges an active thread accumulates before Engram sweeps it
    /// mid-conversation. 0 = interval sweeping disabled (dormant/close remain the only triggers).</summary>
    public int GetEngramTurnInterval() => engram?.TurnsBeforeSweep ?? 0;

    /// <summary>Live update, no restart needed — takes effect on the very next completed exchange.</summary>
    public void SetEngramTurnInterval(int turns)
    {
        if (engram is not null) engram.TurnsBeforeSweep = Math.Max(0, turns);
    }

    // Test-only passthrough — see Engram.DebugExtractOnly.
    public async Task<List<(string Entity, bool IsNew, string Excerpt, bool Sensitive)>> DebugExtractOnly(string threadKey)
        => engram is null ? new() : await engram.DebugExtractOnly(threadKey);

    public ThreadPhase GetThreadPhase(string threadKey) => threadPhases.TryGetValue(threadKey, out ThreadPhase p) ? p : ThreadPhase.Idle;

    // Idle = no thread is currently being processed. Read statically via Activity.IsIdle(); the Scheduler
    // runs background work only while this holds, and long tasks poll it to yield the moment Ari is busy.
    public bool IsIdle => processingThreads.IsEmpty;

    // Ari is "active" whenever any visible (non-Internal) thread is in a state the user can see in the
    // sidebar and is not yet fully settled — Unread (proactive awaiting first reply), Active, Streaming,
    // or Inactive (response window open). Only Dormant and Deleted are settled enough to dream through.
    public bool ConversationActive =>
        threads.Values.Any(t => !t.Internal &&
            t.State is ThreadState.Unread or ThreadState.Active or ThreadState.Streaming or ThreadState.Inactive);

    /// <summary>True when Refactor is loaded and can be run by the Scheduler (the graph walk that replaced BrainScan).</summary>
    public bool HasRefactor => refactor is not null;

    // Canonical persistent-data location (same as the Scheduler tasks use). Only needed by the manual
    // /brainscan and /proactive commands, which don't receive it from ARI.Core.
    private static string PersistentDataDir => Paths.PersistentData;

    /// <summary>Runs a scheduled graph-walk refactor pass, capped at 10 epochs (honours the token so it
    /// yields when cancelled). The manual /refactor command still runs the full uncapped walk.</summary>
    public Task RunRefactorAsync(CancellationToken ct) =>
        refactor?.Run(allNotes: true, ct, epochsOverride: SCHEDULED_REFACTOR_EPOCHS) ?? Task.CompletedTask;

    // The scheduled walk is deliberately short: 10 seeds per run, then it reschedules. Ordering by
    // LastRefactored (oldest first) means each run advances to notes the previous runs never reached.
    private const int SCHEDULED_REFACTOR_EPOCHS = 10;

    /// <summary>
    /// Creates a fresh, owner-facing Dialogue thread whose history is a single assistant message — Ari
    /// speaking first (e.g. a Dream wake). Returns the thread key. The thread is registered like any web
    /// thread (fires the newThread event), so it appears in the sidebar and the owner's reply lands with
    /// the opener in history. Every call rings the owner's phone with a Web Push notification — an
    /// agent-initiated message the owner never prompted for is exactly the case a push exists for.
    /// </summary>
    public string CreateProactiveDialogueThread(string assistantText, string? title = null, string? dreamContext = null)
    {
        string threadKey = $"web-{Guid.NewGuid():N}";
        Thread thread = GetOrCreateThread(ThreadPipeline.Dialogue, threadKey,
            platformContext: string.IsNullOrWhiteSpace(dreamContext) ? null : dreamContext);
        if (!string.IsNullOrWhiteSpace(title)) thread.Title = title;
        thread.AddItem(new Response
        {
            Content   = ContentBlock.Parse(assistantText),
            Timestamp = DateTime.Now,
            State     = State.Complete,
            IsVisible = true,
        });
        thread.StartUnread();   // proactive opener: await the user's reply, else unread → dormant → deleted

        // Best-effort — a missing/failed push must not lose the thread, which is already created either way.
        _ = SendProactivePush(assistantText, threadKey);

        return threadKey;
    }

    private async Task SendProactivePush(string body, string threadKey)
    {
        try { await (Modules.WebPush?.SendPushNotification(body, url: $"/?thread={threadKey}", title: "Ari") ?? Task.CompletedTask); }
        catch (Exception ex) { _logger.LogWarning("[Push] notification failed for thread '{Key}': {Msg}", threadKey, ex.Message); }
    }

    /// <summary>A completed response that nobody was watching live is exactly the case a push exists for
    /// — the owner would otherwise only find out next time they happen to open the app. Internal/guest
    /// threads never ring the phone; a thread with a watcher already saw the reply stream in, so no push.</summary>
    private void PushIfUnwatched(Thread thread, string threadKey)
    {
        if (thread.Internal || !thread.IsOwnerThread) return;
        if (HasWatchers(threadKey)) return;

        string? body = thread.History.OfType<Response>().LastOrDefault(r => r.State == State.Complete)?.ContentText;
        if (string.IsNullOrWhiteSpace(body)) return;

        _ = SendProactivePush(body, threadKey);
    }

    public void NotifyTyping(string threadKey)
    {
        if (threads.TryGetValue(threadKey, out Thread? t)) t.OnUserTyping();
    }

    /// <summary>Returns the Code thread for a given key, creating it if needed, for tool registration.</summary>
    public Thread GetOrCreateCodeThread(string threadKey)
    {
        if (codeArchitect is null) throw new InvalidOperationException("Coder agent not loaded");
        return GetOrCreateThread(ThreadPipeline.Code, threadKey);
    }

    public Thread GetOrCreateDialogueThread(string threadKey)
        => GetOrCreateThread(ThreadPipeline.Dialogue, threadKey);

    // ── Engram eval harness (additive; not used by the live app) ──────────────────────
    // Seeds a dialogue thread with a scripted transcript so a sweep can be tested in isolation,
    // without driving the live Dialogue pipeline turn by turn. RunEngram rebuilds Context from the
    // transcript itself, so pronoun resolution still works.
    public Thread SeedScriptedThread(string threadKey, IReadOnlyList<ThreadMessage> turns)
    {
        Thread thread = GetOrCreateThread(ThreadPipeline.Dialogue, threadKey);
        thread.Seed(turns);
        return thread;
    }

    // Triggers one Engram sweep directly and awaits it. Returns false if Engram isn't loaded.
    public async Task<bool> RunEngramSweepAsync(string threadKey)
    {
        if (engram is null) return false;
        await engram.RunEngram(threadKey, "eval");
        return true;
    }

    public void SetCodeThreadContext(string threadKey, string? projectMap, string? conventions, string? rules)
        => codeArchitect?.SetThreadContext(threadKey, projectMap, conventions, rules);

    /// <summary>Sends a prompt directly through the Code pipeline, bypassing classification.
    /// Used by the desktop client which always needs code-aware responses.</summary>
    public async Task<string> PromptCodeStreaming(
        string              threadKey,
        string              prompt,
        string              username,
        string?             platformContext,
        Func<string, Task>  onDelta,
        CancellationToken   ct        = default,
        string?             localPath = null)
    {
        if (codeArchitect is null) throw new InvalidOperationException("Coder agent not loaded");
        // No outer acquire — codePipeline.ExecuteAsync -> codeArchitect.Prompt already acquires
        // codeArchitect's own queue per step. Same deadlock risk as Route if held here too.
        CancellationTokenSource cts = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();
        processingThreads[threadKey] = cts;
        Thread codeThread = GetOrCreateThread(ThreadPipeline.Code, threadKey, platformContext);
        return await codePipeline!.ExecuteAsync(codeThread, threadKey, prompt, username, platformContext, onDelta, cts, localPath: localPath);
    }

    public (int used, int limit) GetContextStats(string threadKey)
        => textingAgent?.GetContextStats(threads.TryGetValue(threadKey, out Thread? t) ? t : null) ?? (0, 0);

    public void Cancel(string threadKey)
    {
        if (processingThreads.TryGetValue(threadKey, out CancellationTokenSource? cts))
            cts.Cancel();

        // Cancel-cascade: aborting a parent must deterministically abort any live sub-threads
        // (e.g. in-flight Coder steps under a CodeArchitect plan). Recurses through grandchildren.
        if (threads.TryGetValue(threadKey, out Thread? thread))
            foreach (Thread child in thread.Children)
                Cancel(child.Key);
    }

    public void Interrupt(string threadKey)
    {
        if (threads.TryGetValue(threadKey, out Thread? thread)) thread.preserveOnCancel = true;
        Cancel(threadKey);
    }

    /// <summary>Mirror the user's safety toggle onto the thread so the Coder's tool layer can enforce it.
    /// Persistent per thread; CodePipeline clears it when a plan is approved. No-op if the thread doesn't
    /// exist yet (the injected prompt still steers that first turn).</summary>
    public void SetSafeMode(string threadKey, bool on)
    {
        if (threads.TryGetValue(threadKey, out Thread? thread)) thread.SafeMode = on;
    }

    /// <summary>The user jumped in mid-turn ("stop and read this, then continue"). Unlike Interrupt, the turn
    /// is NOT cancelled — the message is queued and the running agent loop folds it into its current chain of
    /// thought (mid-think, or at the next tool-round boundary). The message is added to history immediately so
    /// it shows in the transcript and survives a reload; returns false if the thread isn't currently streaming
    /// (nothing to interject into — the caller should send a normal message instead).</summary>
    public bool Interject(string threadKey, string username, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!threads.TryGetValue(threadKey, out Thread? thread)) return false;
        if (thread.State != ThreadState.Streaming) return false;

        // Insert the visible message just before the in-flight response so the transcript reads in order:
        // prior prompt → this interjection → the response that continues after it.
        int at = thread.streamingResponse is { } sr ? thread.History.IndexOf(sr) : -1;
        Prompt msg = new Prompt { AuthorName = username, Text = text, Timestamp = DateTime.Now, IsVisible = true };
        if (at >= 0) thread.History.Insert(at, msg); else thread.History.Add(msg);

        thread.Interject(username, text);
        thread.RaiseUpdated();
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private void CleanScratchpads()
    {
        string scratchpadRoot = Paths.ServerDir("Scratchpad");
        if (!Directory.Exists(scratchpadRoot)) return;

        int deleted = 0;
        foreach (string dir in Directory.GetDirectories(scratchpadRoot))
        {
            try { Directory.Delete(dir, recursive: true); deleted++; }
            catch (Exception ex) { _logger.LogWarning("[LLM] Scratchpad cleanup failed for {Dir}: {Err}", dir, ex.Message); }
        }

        if (deleted > 0)
            _logger.LogInformation("[LLM] Cleaned up {Count} stale scratchpad(s) from previous run.", deleted);
    }

// ── Data types ───────────────────────────────────────────────────────────────

    public record InternalThreadInfo(string Key, string AgentName, DateTime LastMessageAt, int MessageCount);

    public record LlmCallStat(
        string   AgentName,
        string   ThreadKey,
        DateTime Timestamp,
        int      CompletionTokens,
        int      OutputTokenLimit,
        int      PromptTokens              = 0,
        int      ContextTokenLimit         = 0,
        bool     HadImageAttachments       = false,
        int      EstimatedTextPromptTokens = 0,
        int      ImageTokenLimit           = 0)
    {
        public int EstimatedImageTokens =>
            HadImageAttachments ? Math.Max(0, PromptTokens - EstimatedTextPromptTokens) : 0;
    }
}

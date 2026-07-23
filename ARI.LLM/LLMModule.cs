using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using ARI.Brain;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>An event broadcast to all connected SSE clients over /api/events.</summary>
/// <param name="Type">newThread | streaming | streamingFinished | threadDeleted | threadUpdated</param>
/// <param name="ThreadKey">The thread this event relates to.</param>
/// <param name="Text">Accumulated streaming text — only present for "streaming" events.</param>
public record AppEvent(string Type, string ThreadKey, string? Text = null);

public class LLMModule : ILLMModule, IDisposable
{
    //pipelines
    private readonly DialoguePipeline? dialoguePipeline;
    private readonly CodePipeline?     codePipeline;
    private readonly SpeechPipeline?   speechPipeline;
    
    //agents
    private readonly Dialogue?         dialogue;
    private readonly Coder?            codeArchitect;
    private readonly Memory?           memory;
    private readonly Context?          context;
    private readonly Engram?           engram;
    private readonly Refactor?         refactor;
    private readonly CuriosityAgent?   curiosity;
    private readonly Awareness?        awareness;
    
    
    private readonly CommandService    commands;
    private readonly ConcurrentDictionary<string, CancellationTokenSource>  processingThreads  = new();
    private readonly ConcurrentDictionary<string, LiveCallInfo>            liveCalls           = new();
    private readonly ConcurrentDictionary<Guid, Channel<AppEvent>>         globalSubscribers   = new();
    private readonly Dictionary<string, Agent>                             agentMap            = new();
    private readonly ConcurrentDictionary<string, ThreadPipeline>                                 forcedPipelines   = new();
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

        Dictionary<string, Server> serverByName = servers.ToDictionary(s => s.Name, s => s);

        Dictionary<string, JsonElement> rawAgents = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(agentsJsonPath))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(agentsJsonPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

            // Prompts owned by no single agent: the MemoryAgent block its three children share, and the
            // [Budgets] footer every agent gets. Same file, so the panel edits one place.
            Dictionary<string, string>? sharedMemory = null, sharedBudgets = null, sharedToolSystem = null;
            if (TryGetPropCI(doc.RootElement, "Shared", out JsonElement sharedEl))
            {
                if (TryGetPropCI(sharedEl, "MemoryAgent", out JsonElement memEl))
                    sharedMemory = JsonSerializer.Deserialize<Dictionary<string, string>>(memEl.GetRawText(), JsonOptions);
                if (TryGetPropCI(sharedEl, "Budgets", out JsonElement budEl))
                    sharedBudgets = JsonSerializer.Deserialize<Dictionary<string, string>>(budEl.GetRawText(), JsonOptions);
                if (TryGetPropCI(sharedEl, "ToolSystem", out JsonElement toolEl))
                    sharedToolSystem = JsonSerializer.Deserialize<Dictionary<string, string>>(toolEl.GetRawText(), JsonOptions);
            }
            SharedPrompts.Load(sharedMemory, sharedBudgets, sharedToolSystem);
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
                agent.BoundServer = bound;
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
                agent.BoundServer = first;
            }
            else
            {
                _logger.LogError("Agent '{Agent}' cannot be bound — no servers are configured.", agent.Name);
            }

            if (agent.BoundServer is not null)
            {
                if (agent.SlotName is { Length: > 0 })
                {
                    agent.BoundSlot = agent.BoundServer.Slots.FirstOrDefault(sl => sl.Name.Equals(agent.SlotName, StringComparison.OrdinalIgnoreCase));
                    if (agent.BoundSlot is null)
                        _logger.LogWarning("Agent '{Agent}' names slot '{Slot}' which doesn't exist on server '{Server}' — falling back to its first slot.",
                            agent.Name, agent.SlotName, agent.BoundServer.Name);
                }
                // No name given (or it didn't resolve) — default to the server's first slot, same as the
                // old raw agent.Slot ??= 0 behaviour, so an agent still gets pinned/context-derived out of
                // the box without needing the control panel touched first.
                if (agent.BoundSlot is null && agent.BoundServer.Slots.Count > 0)
                {
                    agent.BoundSlot = agent.BoundServer.Slots[0];
                    agent.SlotName  = agent.BoundSlot.Name;
                }
            }
            return agent;
        }

        if (brainConfig is not null)
        {
            IndexStats brainStats = BrainModule.Initialize(brainConfig);
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
            dialogue = Deserialize<Dialogue>(dialogueEl);
            agentMap["Dialogue"] = dialogue;
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

        if (BrainModule.Ready && rawAgents.TryGetValue("Memory", out JsonElement memoryEl))
        {
            Memory mem = Deserialize<Memory>(memoryEl);
            if (mem.HopLimit > 0)
            {
                memory = mem;
                agentMap["Memory"] = memory;
                _logger.LogInformation("Memory agent is active. Hop limit: {HopLimit}.", memory.HopLimit);
            }
        }

        if (BrainModule.Ready && dialogue is not null)
        {
            if (rawAgents.TryGetValue("Engram", out JsonElement engramEl))
            {
                engram = Deserialize<Engram>(engramEl);
                engram.PersistentDir = PersistentDataDir;
                engram.Registry = threads;
                engram.Notify = NotifyWatchers;
                engram.Init(dialogue, context, threads);
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

            if (rawAgents.TryGetValue("Curiosity", out JsonElement curiosityEl))
            {
                curiosity = Deserialize<CuriosityAgent>(curiosityEl);
                curiosity.PersistentDir = PersistentDataDir;
                curiosity.Registry = threads;
                curiosity.Notify = NotifyWatchers;
                agentMap["Curiosity"] = curiosity;
                _logger.LogInformation("Curiosity is active.");
            }

            commands = new CommandService(engram, refactor);
        }
        else
        {
            commands = new CommandService(engram);
        }

        if (dialogue is not null)
        {
            dialoguePipeline = new DialoguePipeline(dialogue, memory, context, engram, processingThreads, liveCalls, NotifyWatchers);
            dialoguePipeline.ThreadBufferFull    += key => NotifyWatchers(key);
            dialoguePipeline.ThreadBecameInactive += key => NotifyWatchers(key);

            // Speech reuses the Dialogue agent for now (issue #84) — a baseline to diverge from.
            speechPipeline = new SpeechPipeline(dialogue, memory, context, engram, processingThreads, liveCalls, NotifyWatchers);
            speechPipeline.ThreadBufferFull    += key => NotifyWatchers(key);
            speechPipeline.ThreadBecameInactive += key => NotifyWatchers(key);
        }

        if (codeArchitect is not null)
            codePipeline = new CodePipeline(codeArchitect, processingThreads, liveCalls, NotifyWatchers);
    }

    // ── Thread registry ──────────────────────────────────────────────────────────

    private Thread GetOrCreateThread(ThreadPipeline type, string threadKey, string? platformContext = null)
    {
        if (threads.TryGetValue(threadKey, out Thread? existing)) return existing;
        Thread thread = new Thread(type, threadKey, platformContext);
        threads[threadKey] = thread;
        // list_tools/request_tools are always warm (issue #126) and universal — no agent-identity gate.
        // What a group actually resolves to depends on ToolFactories reading this thread's bound context
        // (ProjectRoot etc.), set by whichever agent runs on it (Coder.RunLoop, MemoryAgent.RegisterTools).
        // Threads created outside this choke point (MemoryAgent's internal epoch threads) register their
        // own copy for the same reason.
        new ListTools().Register(thread);
        new RequestTools(thread).Register(thread);
        thread.Updated          += () => Broadcast(new AppEvent("threadUpdated", threadKey));
        thread.Deleted          += () => { threads.TryRemove(threadKey, out _); Broadcast(new AppEvent("threadDeleted", threadKey)); };
        thread.Streaming        += text => Broadcast(new AppEvent("streaming", threadKey, text));
        thread.StreamingFinished += () => Broadcast(new AppEvent("streamingFinished", threadKey));
        // Persist a plain-text transcript to ChatHistory after every completed exchange.
        thread.ExchangeCompleted += (_, _) => ChatHistoryLogger.Write(thread);
        // Engram (or a mark-processed no-op) fires on entry to dormant — the single gate before deletion.
        thread.BecameDormant    += () => OnThreadDormant(thread);
        if (type is ThreadPipeline.Dialogue or ThreadPipeline.Speech && dialogue is not null)
        {
            thread.Deleted        += () => dialogue.RaiseThreadDeleted(threadKey);
            thread.BufferFull     += () => dialogue.RaiseThreadBufferFull(threadKey);
            thread.BecameInactive += () => dialogue.RaiseThreadBecameInactive(threadKey);
            if (context is not null)
                thread.ExchangeCompleted += (user, asst) => _ = context.Update(threadKey, user, asst);
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
        foreach (Attachment attachment in existing.GetAttachments())
            converted.AddAttachment(attachment);
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
        agent.BoundServer = server;
        // A slot name from the old server has no meaning here — re-resolve against the new one, or
        // fall back to unpinned if it doesn't have a same-named slot.
        agent.BoundSlot = agent.SlotName is { Length: > 0 }
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
        agent.BoundSlot = agent.BoundServer is not null && slotName is { Length: > 0 }
            ? agent.BoundServer.Slots.FirstOrDefault(sl => sl.Name.Equals(slotName, StringComparison.OrdinalIgnoreCase))
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
        List<Task> boots = new();
        foreach (Server server in _servers.Where(s => s.BootStartup))
        {
            Model? model = server.CurrentModelName is not null
                ? allModels.FirstOrDefault(m => m.Name.Equals(server.CurrentModelName, StringComparison.OrdinalIgnoreCase))
                : null;
            boots.Add(server.StartAsync(model, modelsPath));
        }
        await Task.WhenAll(boots);
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
    }

    public void AddServer(Server server) => _servers.Add(server);

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
        engram?.Dispose();
        foreach (Server server in _servers)
            server.Dispose();
    }

    // ── Brain backups ───────────────────────────────────────────────────────────
    public bool BrainAvailable => BrainModule.Ready;
    public string BackupBrain()                   => BrainModule.Ready ? BrainModule.Backup()            : "Brain is not available.";
    public List<BackupInfo> ListBrainBackups()    => BrainModule.Ready ? BrainModule.ListBackups()       : new List<BackupInfo>();
    public string RestoreBrainBackup(string file) => BrainModule.Ready ? BrainModule.RestoreBackup(file) : "Brain is not available.";

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

    /// <summary>Binds a project's vault context onto a thread immediately — same fields ChatController
    /// sets for an ObsidianGraph+ServerFs project on the next message, but called synchronously by
    /// bind_project (project_tools) so the model can use filesystem_tools/obsidian_tools THIS turn
    /// instead of waiting for the next one. No-op (root stays unbound) for anything else — a RemoteFs
    /// project's files aren't on this server's disk, same reasoning as Coder.RunLoop's remote branch.</summary>
    public void BindProjectContext(string threadKey, string? rootPath, bool isServerFsVault)
    {
        if (!Threads.TryGetValue(threadKey, out Thread? thread) || !isServerFsVault || rootPath is null) return;
        thread.ProjectRoot  = rootPath;
        thread.IsBrainVault = false;
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
        bool sweep = engram is not null
                  && thread.Pipeline is ThreadPipeline.Dialogue or ThreadPipeline.Speech
                  && thread.HasUserMessages
                  && !thread.EngramProcessed;
        if (!sweep) { thread.EngramProcessed = true; return; }

        _ = Task.Run(async () =>
        {
            try { await engram!.RunEngram(thread.Key, "dormant"); }
            catch (Exception ex) { _logger.LogWarning("[Dormant] Engram failed for {Key}: {Err}", thread.Key, ex.Message); }
        });
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
        if (engram is not null && thread.Pipeline is ThreadPipeline.Dialogue or ThreadPipeline.Speech && thread.HasUserMessages)
        {
            try { await engram.RunEngram(threadKey, "closed", force: true); }
            catch (Exception ex) { _logger.LogWarning("[Close] Engram failed for {Key}: {Err}", threadKey, ex.Message); }
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
        try { return await awareness.IsAddressed(transcript, context, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Awareness] evaluation failed; assuming addressed."); return true; }
    }

    public Task<string> Prompt(string threadKey, string prompt, string username, string? platformContext = null, List<Attachment>? messageAttachments = null, List<Attachment>? threadAttachments = null)
        => Route(threadKey, prompt, username, platformContext, null, CancellationToken.None, messageAttachments, threadAttachments);

    public Task<string> PromptStreaming(string threadKey, string prompt, string username, string? platformContext, Func<string, Task> onDelta, CancellationToken ct = default, List<Attachment>? messageAttachments = null, List<Attachment>? threadAttachments = null, string? localPath = null)
        => Route(threadKey, prompt, username, platformContext, onDelta, ct, messageAttachments, threadAttachments, localPath);

    private async Task<string> Route(string threadKey, string prompt, string username, string? platformContext, Func<string, Task>? onDelta, CancellationToken externalCt, List<Attachment>? messageAttachments = null, List<Attachment>? threadAttachments = null, string? localPath = null)
    {
        if (dialogue is null)
            throw new ModelNotFoundException("Dialogue model is not loaded or is not enabled.");

        
        // New prompt arrived mid-processing — cancel the previous one
        if (IsThreadProcessing(threadKey))
            Interrupt(threadKey);

        // create a cancellation token in case this prompt needs cancelling later
        CancellationTokenSource cts = externalCt.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(externalCt)
            : new CancellationTokenSource();
        processingThreads[threadKey] = cts;

        
        // Discord threads always use Dialogue.
        bool isDiscordThread = threadKey.StartsWith("dm:", StringComparison.OrdinalIgnoreCase)
                            || threadKey.StartsWith("guild:", StringComparison.OrdinalIgnoreCase);
        if (isDiscordThread)
        {
            Thread dlgThread = GetOrCreateThread(ThreadPipeline.Dialogue, threadKey, platformContext);
            return await dialoguePipeline!.ExecuteAsync(dlgThread, threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments, threadAttachments);
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
                return await (codePipeline ?? (Pipeline)dialoguePipeline!).ExecuteAsync(codeThread, threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments, threadAttachments, localPath);
            }
            case "Speech":
            {
                Thread speechThread = Recategorise(ThreadPipeline.Speech, threadKey, platformContext);
                return await (speechPipeline ?? (Pipeline)dialoguePipeline!).ExecuteAsync(speechThread, threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments, threadAttachments, localPath);
            }
            default:
            {
                Thread dlgThread = Recategorise(ThreadPipeline.Dialogue, threadKey, platformContext);
                return await dialoguePipeline!.ExecuteAsync(dlgThread, threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments, threadAttachments);
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
        else if (trimmed == "/curiosity" || trimmed == "/brainscan")
        {
            // /brainscan retained as an alias — the Curiosity agent is BrainScan's successor (graph-walk).
            if (!HasCuriosity) result = "Curiosity is not loaded.";
            else { _ = Task.Run(() => RunCuriosityAsync(CancellationToken.None)); result = "Curiosity walk started — watch the log / Curiosities.json."; }
        }
        else if (trimmed == "/proactive")
        {
            // Manual trigger for the proactive message: pick a curiosity and DM the owner now.
            await RunProactiveMessageAsync(PersistentDataDir, CancellationToken.None);
            result = "Proactive message attempted — check the log and your DMs.";
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
        });
        return handle;
    }

    public bool IsThreadProcessing(string threadKey) => processingThreads.ContainsKey(threadKey);

    public bool IsEngramSweeping(string threadKey) => engram?.IsSweeping(threadKey) ?? false;

    // Idle = no thread is currently being processed. Read statically via Activity.IsIdle(); the Scheduler
    // runs background work only while this holds, and long tasks poll it to yield the moment Ari is busy.
    public bool IsIdle => processingThreads.IsEmpty;

    /// <summary>True when Refactor is loaded and can be run by the Scheduler (the graph walk that replaced BrainScan).</summary>
    public bool HasRefactor => refactor is not null;

    // Canonical persistent-data location (same as the Scheduler tasks use). Only needed by the manual
    // /brainscan and /proactive commands, which don't receive it from ARI.Core.
    private static string PersistentDataDir => Paths.PersistentData;

    /// <summary>Runs a full graph-walk refactor pass (honours the token so it yields when cancelled).</summary>
    public Task RunRefactorAsync(CancellationToken ct) =>
        refactor?.Run(allNotes: true, ct) ?? Task.CompletedTask;

    /// <summary>True when the Curiosity agent is loaded and can be run by the Scheduler.</summary>
    public bool HasCuriosity => curiosity is not null;

    /// <summary>Runs a curiosity walk (idle-gated by the Scheduler; yields when cancelled).</summary>
    public Task RunCuriosityAsync(CancellationToken ct) =>
        curiosity?.Run(ct) ?? Task.CompletedTask;

    /// <summary>
    /// Picks the top pending curiosity, phrases it in Ari's voice, opens a normal Dialogue thread seeded
    /// with that opening message (so Ari can see its own question when the owner replies), and rings the
    /// owner's phone with a Web Push notification. The curiosity is marked "asked" only after the thread is
    /// created, so a failure simply retries next time. Quiet-hours gating is the caller's responsibility.
    /// </summary>
    public async Task RunProactiveMessageAsync(string persistentDir, CancellationToken ct)
    {
        if (dialogue is null) return;

        List<Curiosity> all = CuriosityStore.Load(persistentDir);
        Curiosity? pick = all.Where(c => c.Status == "pending")
            .OrderByDescending(c => c.Priority).ThenBy(c => c.Created)
            .FirstOrDefault();
        if (pick is null) { _logger.LogInformation("[Proactive] no pending curiosities to raise."); return; }

        // Framed to make Ari WRITE its own opening message, not narrate the task. The earlier "Greet them
        // and ask this" phrasing was echoed back as a stage direction ("Ari greets you warmly and asks…");
        // "write your opening message now / output only the message" stops that.
        string instruction = dialogue.PromptText("ProactiveOpener", "", ("question", pick.Question));

        // Generate through the full Dialogue pipeline (not raw SendPrompt) so the opener is grounded in memory
        // recall + context — otherwise Ari phrases blind, with no idea who the people in the question are. The
        // draft thread is internal and never registered, so the instruction above never surfaces in the sidebar;
        // we then seed a fresh owner-facing thread with just the resulting message.
        string opener;
        Thread draft = new(ThreadPipeline.Dialogue, $"proactive-draft:{Guid.NewGuid()}") { Internal = true };
        CancellationTokenSource draftCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        processingThreads[draft.Key] = draftCts;
        try
        {
            opener = dialoguePipeline is not null
                ? await dialoguePipeline.ExecuteAsync(draft, draft.Key, instruction, "user", null, null, draftCts)
                : await dialogue.SendPrompt(draft, instruction, ct: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning("[Proactive] phrasing failed ({Msg}); using the raw question.", ex.Message); opener = pick.Question; }
        finally { processingThreads.TryRemove(draft.Key, out _); }
        if (string.IsNullOrWhiteSpace(opener)) opener = pick.Question;
        opener = opener.Trim();

        // Create the real, owner-facing thread and seed Ari's opening message into its history.
        string threadKey = CreateProactiveDialogueThread(opener, title: pick.Topic);

        // Ring the phone. Web Push is best-effort — a missing/failed push must not lose the thread.
        try { await (Modules.WebPush?.SendPushNotification(opener, url: $"/?thread={threadKey}", title: "Ari") ?? Task.CompletedTask); }
        catch (Exception ex) { _logger.LogWarning("[Proactive] push notification failed: {Msg}", ex.Message); }

        int idx = all.FindIndex(c => c.Id == pick.Id);
        if (idx >= 0) { all[idx] = pick with { Status = "asked", AskedAt = DateTime.UtcNow.ToString("o") }; CuriosityStore.Save(persistentDir, all); }
        _logger.LogInformation("[Proactive] opened thread '{Key}' about '{Topic}'.", threadKey, pick.Topic);
    }

    /// <summary>
    /// Creates a fresh, owner-facing Dialogue thread whose history is a single assistant message — Ari
    /// speaking first. Returns the thread key. The thread is registered like any web thread (fires the
    /// newThread event), so it appears in the sidebar and the owner's reply lands with the opener in history.
    /// </summary>
    public string CreateProactiveDialogueThread(string assistantText, string? title = null)
    {
        string threadKey = $"web-{Guid.NewGuid():N}";
        Thread thread = GetOrCreateThread(ThreadPipeline.Dialogue, threadKey);   // broadcasts "newThread"
        if (!string.IsNullOrWhiteSpace(title)) thread.Title = title;
        thread.AddItem(new Response
        {
            Content   = ContentBlock.Parse(assistantText),
            Timestamp = DateTime.Now,
            State     = State.Complete,
            IsVisible = true,
        });
        thread.StartUnread();   // proactive opener: await the user's reply, else unread → dormant → deleted
        return threadKey;
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
    public Task<string> PromptCodeStreaming(
        string              threadKey,
        string              prompt,
        string              username,
        string?             platformContext,
        Func<string, Task>  onDelta,
        CancellationToken   ct        = default,
        string?             localPath = null)
    {
        if (codeArchitect is null) throw new InvalidOperationException("Coder agent not loaded");
        CancellationTokenSource cts = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();
        processingThreads[threadKey] = cts;
        Thread codeThread = GetOrCreateThread(ThreadPipeline.Code, threadKey, platformContext);
        return codePipeline!.ExecuteAsync(codeThread, threadKey, prompt, username, platformContext, onDelta, cts, localPath: localPath);
    }

    public (int used, int limit) GetContextStats(string threadKey)
        => dialogue?.GetContextStats(threads.TryGetValue(threadKey, out Thread? t) ? t : null) ?? (0, 0);

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

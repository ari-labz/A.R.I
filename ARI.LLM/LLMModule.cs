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
    
    //agents
    private readonly Dialogue?         dialogue;
    private readonly Coder?            code;
    private readonly CodeArchitect?    codeArchitect;
    private readonly Memory?           memory;
    private readonly Context?          context;
    private readonly Engram?           engram;
    private readonly Refactor?         refactor;
    private readonly Classifier?       classifier;
    private readonly Appraisal?        appraiser;
    private readonly BrainModule?      brain;
    
    
    private readonly CommandService    commands;
    private readonly ConcurrentDictionary<string, CancellationTokenSource>  processingThreads  = new();
    private readonly ConcurrentDictionary<string, LiveCallInfo>            liveCalls           = new();
    private readonly ConcurrentDictionary<Guid, Channel<AppEvent>>         globalSubscribers   = new();
    private readonly Dictionary<string, Agent>                             agentMap            = new();
    private readonly HashSet<string>                                                              forcedCodeThreads = new();
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

        Dictionary<string, string> serverEndpoints = servers.ToDictionary(s => s.Name, s => s.FullEndpoint);

        Dictionary<string, JsonElement> rawAgents = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(agentsJsonPath))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(agentsJsonPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            if (TryGetPropCI(doc.RootElement, "Agents", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in arr.EnumerateArray())
                    if (TryGetPropCI(el, "name", out JsonElement nameEl) && nameEl.GetString() is string name)
                        if (TryGetPropCI(el, "enabled", out JsonElement en) && en.GetBoolean())
                            rawAgents[name] = el.Clone();   // Clone: JsonElements must outlive the using-disposed JsonDocument
        }

        T Deserialize<T>(JsonElement el) where T : Agent
        {
            T agent = JsonSerializer.Deserialize<T>(el.GetRawText(), JsonOptions)!;
            if (serverEndpoints.TryGetValue(agent.ServerName, out string? ep))
                agent.Endpoint = ep;
            return agent;
        }

        brain = brainConfig is not null
            ? new BrainModule(brainConfig, loggerFactory)
            : null;

        if (rawAgents.TryGetValue("Context", out JsonElement contextEl))
        {
            context = Deserialize<Context>(contextEl);
            int memoryLimit = rawAgents.TryGetValue("Dialogue", out JsonElement dlgEl)
                ? JsonSerializer.Deserialize<Dialogue>(dlgEl.GetRawText(), JsonOptions)!.ShortTermMemoryLimit ?? 25
                : 25;
            context.Init(memoryLimit);
            _logger.LogInformation("Context tracker is active.");
        }

        if (rawAgents.TryGetValue("Dialogue", out JsonElement dialogueEl))
        {
            dialogue = Deserialize<Dialogue>(dialogueEl);
            agentMap["Dialogue"] = dialogue;
        }

        if (rawAgents.TryGetValue("Coder", out JsonElement codeEl))
        {
            code = Deserialize<Coder>(codeEl);
            agentMap["Code"] = code;
            _logger.LogInformation("Coder agent is active. MaxContext: {Ctx} tokens.", code.MaxContextTokens);
        }

        if (rawAgents.TryGetValue("CodeArchitect", out JsonElement architectEl))
        {
            codeArchitect = Deserialize<CodeArchitect>(architectEl);
            agentMap["CodeArchitect"] = codeArchitect;
            _logger.LogInformation("CodeArchitect agent is active. MaxContext: {Ctx} tokens.", codeArchitect.MaxContextTokens);
        }

        if (rawAgents.TryGetValue("Classifier", out JsonElement classifierEl))
        {
            classifier = Deserialize<Classifier>(classifierEl);
            _logger.LogInformation("Classifier is active.");
        }

        if (rawAgents.TryGetValue("Appraisal", out JsonElement appraiserEl))
        {
            appraiser = Deserialize<Appraisal>(appraiserEl);
            _logger.LogInformation("Appraisal is active.");
        }

        // The architect appraises each plan turn to decide its thinking budget (null appraiser ⇒ no thinking).
        if (codeArchitect is not null) codeArchitect.Appraisal = appraiser;

        if (brain is not null && rawAgents.TryGetValue("Memory", out JsonElement memoryEl))
        {
            Memory mem = Deserialize<Memory>(memoryEl);
            if (mem.RecursiveBrainSearchDepth > 0)
            {
                memory = mem;
                memory.brain          = brain;
                memory.brainPublicUrl = brain.BrainPublicUrl;
                agentMap["Memory"] = memory;
                _logger.LogInformation("Memory agent is active. Depth: {Depth}.", memory.RecursiveBrainSearchDepth);
            }
        }

        if (brain is not null && dialogue is not null)
        {
            if (rawAgents.TryGetValue("Engram", out JsonElement engramEl))
            {
                engram = Deserialize<Engram>(engramEl);
                engram.Init(dialogue, brain, context, brain.BrainPublicUrl, threads);
                engram.SweepCompleted += key => NotifyWatchers(key);
                agentMap["Engram"] = engram;
                _logger.LogInformation("Engram is active. Brain connected.");
            }

            if (rawAgents.TryGetValue("Refactor", out JsonElement refactorEl))
            {
                refactor = Deserialize<Refactor>(refactorEl);
                refactor.brain  = brain;
                refactor.engram = engram;
                agentMap["Refactor"] = refactor;
                _logger.LogInformation("Refactor is active.");
            }

            commands = new CommandService(engram, refactor, brain.PurgeAllNotes, brain.Backup, brain.GetDirtyNotes);
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
        }

        if (code is not null)
            codePipeline = new CodePipeline(code, codeArchitect, processingThreads, liveCalls, NotifyWatchers);
    }

    // ── Thread registry ──────────────────────────────────────────────────────────

    private Thread GetOrCreateThread(ThreadPipeline type, string threadKey, string? platformContext = null)
    {
        if (threads.TryGetValue(threadKey, out Thread? existing)) return existing;
        Thread thread = new Thread(type, threadKey, platformContext);
        threads[threadKey] = thread;
        thread.Updated          += () => Broadcast(new AppEvent("threadUpdated", threadKey));
        thread.Deleted          += () => { threads.TryRemove(threadKey, out _); Broadcast(new AppEvent("threadDeleted", threadKey)); };
        thread.Streaming        += text => Broadcast(new AppEvent("streaming", threadKey, text));
        thread.StreamingFinished += () => Broadcast(new AppEvent("streamingFinished", threadKey));
        // Persist a plain-text transcript to ARI/chat_history after every completed exchange.
        thread.ExchangeCompleted += (_, _) => ChatHistoryLogger.Write(thread);
        if (type == ThreadPipeline.Code)
            thread.BecameInactive += () => thread.MarkEngramProcessed();
        if (type == ThreadPipeline.Dialogue && dialogue is not null)
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
    /// to GetOrCreateThread when no thread exists yet. Safe to call at any point in a thread's life — the
    /// classifier currently only invokes it on the first message, but nothing here assumes that.
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
        agent.ServerName = server.Name;
        agent.Endpoint   = server.FullEndpoint;
        return true;
    }

    /// <summary>
    /// Assigns a specific llama-server slot index to a live agent. Pass null to clear.
    /// Returns false if the agent is not found.
    /// </summary>
    public bool AssignAgentSlot(string agentName, int? slot)
    {
        if (!agentMap.TryGetValue(agentName, out Agent? agent)) return false;
        agent.Slot = slot;
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
    public bool BrainAvailable => brain is not null;
    public Task<string> BackupBrain()                  => brain?.Backup()           ?? Task.FromResult("Brain is not available.");
    public List<BackupInfo> ListBrainBackups()         => brain?.ListBackups()      ?? new List<BackupInfo>();
    public Task<string> RestoreBrainBackup(string file) => brain?.RestoreBackup(file) ?? Task.FromResult("Brain is not available.");

    // ── Prompting ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Pre-marks a thread to run through the Code pipeline, bypassing the classifier.
    /// Call before the first PromptStreaming for project threads with ForceCodePipeline enabled.
    /// </summary>
    public void ForceCodeThread(string threadKey)
    {
        forcedCodeThreads.Add(threadKey);
        GetOrCreateThread(ThreadPipeline.Code, threadKey);
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

        
        // Discord threads always use Dialogue — never run them through the Classifier
        bool isDiscordThread = threadKey.StartsWith("dm:", StringComparison.OrdinalIgnoreCase)
                            || threadKey.StartsWith("guild:", StringComparison.OrdinalIgnoreCase);
        if (isDiscordThread || classifier is null)
        {
            Thread dlgThread = GetOrCreateThread(ThreadPipeline.Dialogue, threadKey, platformContext);
            return await dialoguePipeline!.ExecuteAsync(dlgThread, threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments, threadAttachments);
        }

        // if the thread already exists, its type tells us which pipeline owns it
        threads.TryGetValue(threadKey, out Thread? existing);
        string? agent = existing?.Pipeline.ToString();
        bool hasMessages = existing is { History.Count: > 0 };

        // new or empty thread (may exist for attachment staging) needs classifying
        if (!hasMessages)
        {
            if (forcedCodeThreads.Contains(threadKey))
            {
                agent = "Code";
                _logger.LogInformation($"[Classifier] ({threadKey}) → Code (forced by project)");
            }
            else
            {
                agent = await classifier.Classify(prompt, cts.Token);
                _logger.LogInformation($"[Classifier] ({threadKey}) → {agent}");
            }

        }

        switch (agent)
        {
            case "Code":
            {
                Thread codeThread = Recategorise(ThreadPipeline.Code, threadKey, platformContext);
                return await (codePipeline ?? (Pipeline)dialoguePipeline!).ExecuteAsync(codeThread, threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments, threadAttachments, localPath);
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
                if (item is AriResponse resp && (resp.CompletionTokens > 0 || resp.PromptTokens > 0))
                    result.Add(new LlmCallStat(entry.Value.Pipeline.ToString(), entry.Key, resp.Timestamp,
                        resp.CompletionTokens, resp.OutputTokenLimit,
                        resp.PromptTokens, resp.ContextTokenLimit,
                        resp.HadImageAttachments, resp.EstimatedTextPromptTokens,
                        resp.ImageTokenLimit));
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

    public void NotifyTyping(string threadKey)
    {
        if (threads.TryGetValue(threadKey, out Thread? t)) t.ResetInactivityTimer();
    }

    /// <summary>Returns the Code thread for a given key, creating it if needed, for tool registration.</summary>
    public Thread GetOrCreateCodeThread(string threadKey)
    {
        if (code is null) throw new InvalidOperationException("Code agent not loaded");
        return GetOrCreateThread(ThreadPipeline.Code, threadKey);
    }

    public Thread GetOrCreateDialogueThread(string threadKey)
        => GetOrCreateThread(ThreadPipeline.Dialogue, threadKey);

    public void SetCodeThreadContext(string threadKey, string? projectMap, string? conventions, string? rules)
        => code?.SetThreadContext(threadKey, projectMap, conventions, rules);

    public void RegisterUpdateTodos(Thread thread)
    {
        if (code is null) return;
        new UpdateTodos(code, thread).Register(thread);
    }

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
        if (code is null) throw new InvalidOperationException("Code agent not loaded");
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

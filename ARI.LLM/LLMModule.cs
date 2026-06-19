using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using ARI.Brain;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public class LLMModule : ILLMModule, IDisposable
{
    //pipelines
    private readonly DialoguePipeline? dialoguePipeline;
    private readonly CodePipeline?     codePipeline;
    
    //agents
    private readonly Dialogue?         dialogue;
    private readonly Code?             code;
    private readonly Memory?           memory;
    private readonly Context?          context;
    private readonly Engram?           engram;
    private readonly Refactor?         refactor;
    private readonly Classifier?       classifier;
    private readonly BrainModule?      brain;
    
    
    private readonly CommandService    commands;
    private readonly ConcurrentDictionary<string, CancellationTokenSource>                      processingThreads = new();
    private readonly ConcurrentDictionary<string, LiveCallInfo>                                  liveCalls         = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<bool?>>>   threadWatchers    = new();
    private readonly Dictionary<string, Agent>                                                   agentMap          = new();
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

    /// <summary>Every thread across all pipelines, keyed by thread key. Each carries its <see cref="ThreadType"/>.</summary>
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
            ILogger serverLogger = loggerFactory.CreateLogger("ARI.Brain");
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

        if (rawAgents.TryGetValue("Code", out JsonElement codeEl))
        {
            code = Deserialize<Code>(codeEl);
            agentMap["Code"] = code;
            _logger.LogInformation("Code agent is active. MaxContext: {Ctx} tokens.", code.MaxContextTokens);
        }

        if (rawAgents.TryGetValue("Classifier", out JsonElement classifierEl))
        {
            classifier = Deserialize<Classifier>(classifierEl);
            _logger.LogInformation("Classifier is active.");
        }

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
            codePipeline = new CodePipeline(code, processingThreads, liveCalls, NotifyWatchers);
    }

    // ── Thread registry ──────────────────────────────────────────────────────────

    private Thread GetOrCreateThread(ThreadType type, string threadKey, string? platformContext = null)
    {
        if (threads.TryGetValue(threadKey, out Thread? existing)) return existing;
        Thread thread = new Thread(type, threadKey, platformContext);
        threads[threadKey] = thread;
        thread.Updated += () => NotifyWatchers(threadKey);
        thread.Deleted += () => { threads.TryRemove(threadKey, out _); NotifyThreadDeleted(threadKey); };
        if (type == ThreadType.Code)
            thread.BecameInactive += () => thread.MarkEngramProcessed();
        if (type == ThreadType.Dialogue && dialogue is not null)
        {
            thread.Deleted        += () => dialogue.RaiseThreadDeleted(threadKey);
            thread.BufferFull     += () => dialogue.RaiseThreadBufferFull(threadKey);
            thread.BecameInactive += () => dialogue.RaiseThreadBecameInactive(threadKey);
            if (context is not null)
                thread.ExchangeCompleted += (user, asst) => _ = context.Update(threadKey, user, asst);
        }
        return thread;
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
        GetOrCreateThread(ThreadType.Code, threadKey);
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
            Thread dlgThread = GetOrCreateThread(ThreadType.Dialogue, threadKey, platformContext);
            return await dialoguePipeline!.ExecuteAsync(dlgThread, threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments, threadAttachments);
        }

        // if the thread already exists, its type tells us which pipeline owns it
        threads.TryGetValue(threadKey, out Thread? existing);
        string? agent = existing?.Type.ToString();
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

            // Pre-create the thread so watchers receive isCodeMode before the LLM starts responding.
            if (agent == "Code")
                GetOrCreateThread(ThreadType.Code, threadKey, platformContext);
        }

        switch (agent)
        {
            case "Code":
            {
                Thread codeThread = GetOrCreateThread(ThreadType.Code, threadKey, platformContext);
                return await (codePipeline ?? (Pipeline)dialoguePipeline!).ExecuteAsync(codeThread, threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments, threadAttachments, localPath);
            }
            default:
            {
                Thread dlgThread = GetOrCreateThread(ThreadType.Dialogue, threadKey, platformContext);
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

    // ── Internal watcher infrastructure ────────────────────────────────────────

    private void NotifyWatchers(string threadKey)
    {
        if (!threadWatchers.TryGetValue(threadKey, out ConcurrentDictionary<Guid, Channel<bool?>>? watchers)) return;
        foreach (Channel<bool?> ch in watchers.Values)
            ch.Writer.TryWrite(true);
    }

    private void NotifyThreadDeleted(string threadKey)
    {
        if (!threadWatchers.TryGetValue(threadKey, out ConcurrentDictionary<Guid, Channel<bool?>>? watchers)) return;
        foreach (Channel<bool?> ch in watchers.Values)
            ch.Writer.TryWrite(null);
    }

    private sealed class WatcherHandle(
        ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<bool?>>> registry,
        string threadKey, Guid id, Channel<bool?> channel) : IDisposable
    {
        public void Dispose()
        {
            if (registry.TryGetValue(threadKey, out ConcurrentDictionary<Guid, Channel<bool?>>? watchers))
                watchers.TryRemove(id, out _);
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
                    result.Add(new LlmCallStat(entry.Value.Type.ToString(), entry.Key, resp.Timestamp,
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
        Guid id = Guid.NewGuid();
        threadWatchers.GetOrAdd(threadKey, _ => new())[id] = channel;
        return new WatcherHandle(threadWatchers, threadKey, id, channel);
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
        return GetOrCreateThread(ThreadType.Code, threadKey);
    }

    public Thread GetOrCreateDialogueThread(string threadKey)
        => GetOrCreateThread(ThreadType.Dialogue, threadKey);

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
        Thread codeThread = GetOrCreateThread(ThreadType.Code, threadKey, platformContext);
        return codePipeline!.ExecuteAsync(codeThread, threadKey, prompt, username, platformContext, onDelta, cts, localPath: localPath);
    }

    public (int used, int limit) GetContextStats(string threadKey)
        => dialogue?.GetContextStats(threads.TryGetValue(threadKey, out Thread? t) ? t : null) ?? (0, 0);

    public void Cancel(string threadKey)
    {
        if (processingThreads.TryGetValue(threadKey, out CancellationTokenSource? cts))
            cts.Cancel();
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

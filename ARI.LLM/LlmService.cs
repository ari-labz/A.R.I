using System.Collections.Concurrent;
using System.Threading.Channels;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public class LlmService : IDisposable
{
    private readonly Dialogue?    dialogue;
    private readonly Code?        code;
    private readonly Memory?      memory;
    private readonly Context?     context;
    private readonly Engram?      engram;
    private readonly Refactor?    refactor;
    private readonly Classifier?  classifier;
    private readonly BrainModule? brain;
    private readonly CommandService commands;

    private readonly ConcurrentDictionary<string, CancellationTokenSource>                      processingThreads = new();
    private readonly ConcurrentDictionary<string, LiveCallInfo>                                  liveCalls         = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<bool?>>>   threadWatchers    = new();
    private readonly Dictionary<string, Agent>                                                   agentMap          = new();
    private readonly HashSet<string>                                                              forcedCodeThreads = new();

    // The single flat registry of every thread, across all pipelines. Agents hold a reference to
    // this and expose type-filtered views; the service navigates it directly.
    private readonly ConcurrentDictionary<string, Thread>                                         threads           = new();

    /// <summary>All active agents, keyed by name. Navigate here to access threads and their data.</summary>
    public IReadOnlyDictionary<string, Agent> Agents => agentMap;

    /// <summary>Every thread across all pipelines, keyed by thread key. Each carries its <see cref="ThreadType"/>.</summary>
    public IReadOnlyDictionary<string, Thread> Threads => threads;

    public LlmService(IReadOnlyList<AgentConfig> agents, IReadOnlyDictionary<string, string>? serverEndpoints = null, BrainConfig? brainConfig = null, ILoggerFactory? loggerFactory = null)
    {
        if (loggerFactory is not null)
            Common.InitialiseLogger(loggerFactory);

        // Resolve ServerName → endpoint for each agent before construction
        if (serverEndpoints is not null)
            foreach (AgentConfig cfg in agents)
                if (serverEndpoints.TryGetValue(cfg.ServerName, out string? ep))
                    cfg.Endpoint = ep;

        Dictionary<string, AgentConfig> enabledAgents = agents
            .Where(m => m.Enabled)
            .ToDictionary(m => m.Name);

        brain = brainConfig is not null
            ? new BrainModule(brainConfig, loggerFactory)
            : null;

        // Context is created first so it can be passed to Dialogue, which subscribes each new
        // thread's ExchangeCompleted event to fire context.Update in the background.
        if (enabledAgents.TryGetValue("Context", out AgentConfig? contextConfig))
        {
            int memoryLimit = enabledAgents.TryGetValue("Dialogue", out AgentConfig? dlgCfg) ? dlgCfg.ShortTermMemoryLimit : 25;
            context = new Context(contextConfig, memoryLimit);
            context.AttachRegistry(threads);
            Common.Logger.LogInformation("Context tracker is active.");
        }

        if (enabledAgents.TryGetValue("Dialogue", out AgentConfig? dialogueConfig))
        {
            dialogue = new Dialogue(dialogueConfig, context);
            dialogue.AttachRegistry(threads);
            dialogue.ThreadUpdated += key => NotifyWatchers(key);
            dialogue.ThreadDeleted += key => NotifyThreadDeleted(key);
            agentMap["Dialogue"] = dialogue;
        }

        if (enabledAgents.TryGetValue("Code", out AgentConfig? codeConfig))
        {
            code = new Code(codeConfig);
            code.AttachRegistry(threads);
            code.ThreadUpdated += key => NotifyWatchers(key);
            code.ThreadDeleted += key => NotifyThreadDeleted(key);
            agentMap["Code"] = code;
            Common.Logger.LogInformation("Code agent is active. MaxContext: {Ctx} tokens.", codeConfig.MaxContextTokens);
        }

        if (enabledAgents.TryGetValue("Classifier", out AgentConfig? classifierConfig))
        {
            classifier = new Classifier(classifierConfig);
            classifier.AttachRegistry(threads);
            Common.Logger.LogInformation("Classifier is active.");
        }

        if (brain is not null && enabledAgents.TryGetValue("Memory", out AgentConfig? memoryConfig) && memoryConfig.RecursiveBrainSearchDepth > 0)
        {
            memory = new Memory(memoryConfig, brain, memoryConfig.RecursiveBrainSearchDepth, brain.BrainPublicUrl);
            memory.AttachRegistry(threads);
            agentMap["Memory"] = memory;
            Common.Logger.LogInformation("Memory agent is active. Depth: {Depth}.", memoryConfig.RecursiveBrainSearchDepth);
        }

        if (brain is not null && dialogue is not null)
        {
            if (enabledAgents.TryGetValue("Engram", out AgentConfig? engramConfig))
            {
                engram = new Engram(engramConfig, dialogue, brain, context, engramConfig.RecursiveBrainSearchDepth, brain.BrainPublicUrl);
                engram.AttachRegistry(threads);
                engram.SweepCompleted += key => NotifyWatchers(key);
                agentMap["Engram"] = engram;
                Common.Logger.LogInformation("Engram is active. Brain connected.");
            }

            if (enabledAgents.TryGetValue("Refactor", out AgentConfig? refactorConfig))
            {
                refactor = new Refactor(refactorConfig, brain, engram);
                refactor.AttachRegistry(threads);
                agentMap["Refactor"] = refactor;
                Common.Logger.LogInformation("Refactor is active.");
            }

            commands = new CommandService(engram, refactor, brain.PurgeAllNotes, brain.Backup, brain.GetDirtyNotes);
        }
        else
        {
            commands = new CommandService(engram);
        }
    }

    public void Dispose() => engram?.Dispose();

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
        code?.GetOrCreateThread(threadKey);
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
            return await DialoguePipeline(threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments, threadAttachments);

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
                Common.Logger.LogInformation($"[Classifier] ({threadKey}) → Code (forced by project)");
            }
            else
            {
                agent = await classifier.Classify(prompt, cts.Token);
                Common.Logger.LogInformation($"[Classifier] ({threadKey}) → {agent}");
            }

            // Pre-create the thread on the correct agent so watchers receive isCodeMode
            // before the LLM starts responding — this triggers the animation immediately.
            if (agent == "Code")
                code?.GetOrCreateThread(threadKey);
        }

        switch (agent)
        {
            case "Code":
                return await CodePipeline(threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments, threadAttachments, localPath);
            default:
                return await DialoguePipeline(threadKey, prompt, username, platformContext, onDelta, cts, messageAttachments, threadAttachments);
        }
        
    }

    private async Task<string> DialoguePipeline(string threadKey, string prompt, string username, string? platformContext, Func<string, Task>? onDelta, CancellationTokenSource cts, List<Attachment>? messageAttachments = null, List<Attachment>? threadAttachments = null)
    {
        if (engram?.IsSweeping(threadKey) == true)
        {
            Common.Logger.LogInformation("[Dialogue] ({Thread}) waiting for Engram sweep to finish before processing.", threadKey);
            await engram.WaitForSweep(threadKey, cts.Token);
        }

        Thread dialogueThread = dialogue!.GetOrCreateThread(threadKey);
        if (threadAttachments is { Count: > 0 })
            foreach (Attachment a in threadAttachments) dialogueThread.AddAttachment(a);

        LiveCallInfo liveCall = new("Dialogue", threadKey, 0, dialogue.MaxTokens, dialogue.MaxContextTokens, dialogue.MaxImageTokens);
        liveCalls[threadKey] = liveCall;
        dialogueThread.SetLiveCall(liveCall);

        string effectivePrompt = dialogueThread.History.Count > 0 && dialogueThread.History[^1] is UserMessage prev
            ? prev.Content + "\n" + prompt
            : prompt;

        dialogueThread.AddItem(new UserMessage
        {
            Username    = username,
            Content     = prompt,
            Timestamp   = DateTime.Now,
            Attachments = messageAttachments is { Count: > 0 } ? messageAttachments : null
        });

        try
        {
            Common.Logger.LogInformation("[Dialogue] ({Thread}) prompt\n\"{Prompt}\"", threadKey, prompt);

            string? contextSummary = context?.GetContext(threadKey);

            string? recallBlock = null;
            if (memory is not null)
            {
                try
                {
                    List<ThreadMessage> chatHistory = dialogueThread.GetChatHistory();
                    recallBlock = await memory.GetNotes(chatHistory, effectivePrompt, contextSummary, cts.Token);
                }
                catch (Exception ex)
                {
                    Common.Logger.LogError("[Memory] Failed to retrieve memories: {Error}", ex.Message);
                    string errorMessage = "> error retrieving memories";
                    if (onDelta is not null) await onDelta(errorMessage);
                    return errorMessage;
                }
            }

            return await dialogue.SendPrompt(threadKey, effectivePrompt, username, platformContext, recallBlock, contextSummary, cts.Token, userMessagePreadded: true, onDelta: onDelta);
        }
        catch (OperationCanceledException)
        {
            dialogueThread.preserveOnCancel = false;
            throw;
        }
        finally
        {
            liveCalls.TryRemove(threadKey, out _);
            dialogueThread.ClearMessageAttachments();
            processingThreads.TryRemove(new KeyValuePair<string, CancellationTokenSource>(threadKey, cts));
            cts.Dispose();
            NotifyWatchers(threadKey);
        }
    }

    private async Task<string> CodePipeline(string threadKey, string prompt, string username, string? platformContext, Func<string, Task>? onDelta, CancellationTokenSource cts, List<Attachment>? messageAttachments = null, List<Attachment>? threadAttachments = null, string? localPath = null)
    {
        if (code is null)
        {
            Common.Logger.LogWarning("[Code] ({Thread}) Code agent not enabled, falling back to Dialogue.", threadKey);
            return await DialoguePipeline(threadKey, prompt, username, platformContext, onDelta, cts);
        }

        Thread codeThread = code.GetOrCreateThread(threadKey);
        if (threadAttachments is { Count: > 0 })
            foreach (Attachment a in threadAttachments) codeThread.AddAttachment(a);

        if (!string.IsNullOrWhiteSpace(localPath))
        {
            string resolvedRoot = Path.GetFullPath(localPath);
            new PreviewFile(resolvedRoot, cts.Token).Register(codeThread);
            new ReadFile(resolvedRoot, cts.Token).Register(codeThread);
            new ListDirectory(resolvedRoot, cts.Token).Register(codeThread);
            new SearchFiles(resolvedRoot, cts.Token).Register(codeThread);
            new FindFiles(resolvedRoot, cts.Token).Register(codeThread);
            new EditFile(resolvedRoot, cts.Token).Register(codeThread);
            new WriteFile(resolvedRoot, cts.Token).Register(codeThread);
            new DeleteFile(resolvedRoot, cts.Token).Register(codeThread);
            new MoveFile(resolvedRoot, cts.Token).Register(codeThread);
            new UpdateTodos(codeThread).Register(codeThread);
        }

        LiveCallInfo liveCall = new("Code", threadKey, 0, code.MaxTokens, code.MaxContextTokens, 0);
        liveCalls[threadKey] = liveCall;
        codeThread.SetLiveCall(liveCall);

        string effectivePrompt = codeThread.History.Count > 0 && codeThread.History[^1] is UserMessage prev
            ? prev.Content + "\n" + prompt
            : prompt;

        codeThread.AddItem(new UserMessage
        {
            Username    = username,
            Content     = prompt,
            Timestamp   = DateTime.Now,
            Attachments = messageAttachments is { Count: > 0 } ? messageAttachments : null
        });

        try
        {
            Common.Logger.LogInformation("[Code] ({Thread}) prompt\n\"{Prompt}\"", threadKey, prompt);
            return await code.SendPrompt(threadKey, effectivePrompt, username, platformContext, cts.Token, userMessagePreadded: true, onDelta: onDelta);
        }
        catch (OperationCanceledException)
        {
            codeThread.preserveOnCancel = false;
            throw;
        }
        finally
        {
            liveCalls.TryRemove(threadKey, out _);
            codeThread.ClearMessageAttachments();
            processingThreads.TryRemove(new KeyValuePair<string, CancellationTokenSource>(threadKey, cts));
            cts.Dispose();
            NotifyWatchers(threadKey);
        }
    }

    // ── Commands ────────────────────────────────────────────────────────────────

    public async Task<string?> HandleCommand(string? threadKey, string input)
    {
        // Show the input straight away to acknowledge the command, then run it.
        if (threadKey is not null)
            dialogue?.AddCommandInput(threadKey, input);

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

        if (threadKey is not null)
        {
            if (result is not null) dialogue?.AddCommandResponse(threadKey, result);
            else                    dialogue?.DropCommandInput(threadKey);   // unrecognised — undo the input
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

    public void NotifyTyping(string threadKey) => dialogue?.NotifyTyping(threadKey);

    /// <summary>Returns the Code thread for a given key, creating it if needed, for tool registration.</summary>
    public Thread GetOrCreateCodeThread(string threadKey)
        => code?.GetOrCreateThread(threadKey) ?? throw new InvalidOperationException("Code agent not loaded");

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
        return CodePipeline(threadKey, prompt, username, platformContext, onDelta, cts, localPath: localPath);
    }

    public (int used, int limit) GetContextStats(string threadKey)
        => dialogue?.GetContextStats(dialogue.GetThread(threadKey)) ?? (0, 0);

    public void Cancel(string threadKey)
    {
        if (processingThreads.TryGetValue(threadKey, out CancellationTokenSource? cts))
            cts.Cancel();
    }

    public void Interrupt(string threadKey)
    {
        Thread? thread = dialogue?.GetThread(threadKey);
        if (thread is not null) thread.preserveOnCancel = true;
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

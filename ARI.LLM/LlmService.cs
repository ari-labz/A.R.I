using System.Collections.Concurrent;
using System.Threading.Channels;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public class LlmService : IDisposable
{
    private readonly Dialogue?   dialogue;
    private readonly Context?    context;
    private readonly Recall?     recall;
    private readonly Engram?     engram;
    private readonly Refactor?   refactor;
    private readonly CommandService commands;
    private readonly ConcurrentDictionary<string, byte> processingThreads = new();

    // Per-thread watcher channels — each connected client gets one.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<bool>>> threadWatchers = new();

    public LlmService(string agentsConfigPath, string? brainConfigPath = null, ILoggerFactory? loggerFactory = null)
    {
        if (loggerFactory is not null)
            Common.InitialiseLogger(loggerFactory);

        AriAgentsConfig config = AriAgentsConfig.LoadFrom(agentsConfigPath);

        Dictionary<string, AgentConfig> enabled = config.Agents
            .Where(m => m.Enabled)
            .ToDictionary(m => m.Name);

        if (enabled.TryGetValue("Dialogue", out AgentConfig? dialogueConfig))
        {
            dialogue = new Dialogue(dialogueConfig);
            dialogue.ThreadUpdated += key => NotifyWatchers(key);
        }

        if (enabled.TryGetValue("Context", out AgentConfig? contextConfig))
        {
            int memoryLimit = enabled.TryGetValue("Dialogue", out AgentConfig? dlgCfg) ? dlgCfg.ShortTermMemoryLimit : 25;
            context = new Context(contextConfig, memoryLimit);
            Common.Logger.LogInformation("Context tracker is active.");
        }

        if (brainConfigPath is not null && dialogue is not null)
        {
            BrainService brain = new BrainService(brainConfigPath, loggerFactory);

            if (enabled.TryGetValue("Recall", out AgentConfig? recallConfig) && recallConfig.RecursiveBrainSearchDepth > 0)
            {
                recall = new Recall(recallConfig, brain, recallConfig.RecursiveBrainSearchDepth, brain.BrainPublicUrl);
                Common.Logger.LogInformation("Recall is active. Depth: {Depth}, Cache: {Cache}.",
                    recallConfig.RecursiveBrainSearchDepth, recallConfig.CacheSize > 0 ? recallConfig.CacheSize : 0);
            }

            if (enabled.TryGetValue("Engram", out AgentConfig? engramConfig))
            {
                engram = new Engram(engramConfig, dialogue, brain, context, engramConfig.SweepIntervalMinutes, engramConfig.RecursiveBrainSearchDepth, brain.BrainPublicUrl);
                Common.Logger.LogInformation("Engram is active. Brain connected.");
            }

            if (enabled.TryGetValue("Refactor", out AgentConfig? refactorConfig))
            {
                refactor = new Refactor(refactorConfig, brain, engram);
                Common.Logger.LogInformation("Refactor is active.");
            }

            commands = new CommandService(engram, refactor, brain.PurgeAllNotes, brain.Backup, brain.GetDirtyNotes);
        }
        else
        {
            commands = new CommandService(engram);
        }
    }

    // ── Thread accessors (client-facing) ────────────────────────────────────────

    public IReadOnlyCollection<string> GetActiveThreadKeys()
        => dialogue?.ThreadKeys ?? Array.Empty<string>();

    /// <summary>Returns the typed ThreadItem list for a dialogue thread. This is what clients render.</summary>
    public IReadOnlyList<ThreadItem> GetThreadItems(string threadKey)
        => dialogue?.GetThread(threadKey)?.History ?? [];

    /// <summary>Returns the typed ThreadItem list for an internal agent thread (Engram, Refactor, etc.).</summary>
    public IReadOnlyList<ThreadItem> GetInternalThreadItems(string threadKey)
    {
        if (engram?.ThreadKeys.Contains(threadKey)   == true) return engram.GetThread(threadKey)?.History   ?? [];
        if (refactor?.ThreadKeys.Contains(threadKey) == true) return refactor.GetThread(threadKey)?.History ?? [];
        if (recall?.ThreadKeys.Contains(threadKey)   == true) return recall.GetThread(threadKey)?.History   ?? [];
        if (context?.ThreadKeys.Contains(threadKey)  == true) return context.GetThread(threadKey)?.History  ?? [];
        return [];
    }

    public DateTime GetThreadLastMessageAt(string threadKey)
        => dialogue?.GetThread(threadKey)?.LastMessageAt ?? DateTime.MinValue;

    // Returns metadata for all internal agent threads (Engram, Recall, Context, Refactor).
    // Threads sharing a key with a Dialogue thread are excluded to avoid duplication.
    public IReadOnlyList<InternalThreadInfo> GetInternalThreads()
    {
        List<InternalThreadInfo> result = new();
        HashSet<string> dialogueKeys = new(dialogue?.ThreadKeys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        void Add(Agent? agent, string agentName)
        {
            if (agent is null) return;
            foreach (string key in agent.ThreadKeys)
            {
                if (dialogueKeys.Contains(key)) continue;
                Thread? t = agent.GetThread(key);
                result.Add(new InternalThreadInfo(key, agentName,
                    t?.LastMessageAt ?? DateTime.MinValue,
                    t?.History?.Count ?? 0));
            }
        }

        Add(engram,   "Engram");
        Add(refactor, "Refactor");
        Add(recall,   "Recall");
        Add(context,  "Context");
        return result;
    }

    public record InternalThreadInfo(string Key, string AgentName, DateTime LastMessageAt, int MessageCount);

    // ── Thread watchers (server push) ───────────────────────────────────────────

    /// <summary>
    /// Subscribes a client channel to receive notifications whenever the given thread's history changes.
    /// Dispose the returned handle to unsubscribe (called when the client disconnects).
    /// </summary>
    public IDisposable WatchThread(string threadKey, Channel<bool> channel)
    {
        Guid id = Guid.NewGuid();
        threadWatchers.GetOrAdd(threadKey, _ => new())[id] = channel;
        return new WatcherHandle(threadWatchers, threadKey, id, channel);
    }

    private void NotifyWatchers(string threadKey)
    {
        if (!threadWatchers.TryGetValue(threadKey, out ConcurrentDictionary<Guid, Channel<bool>>? watchers)) return;
        foreach (Channel<bool> ch in watchers.Values)
            ch.Writer.TryWrite(true);
    }

    private sealed class WatcherHandle(
        ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<bool>>> registry,
        string threadKey, Guid id, Channel<bool> channel) : IDisposable
    {
        public void Dispose()
        {
            if (registry.TryGetValue(threadKey, out ConcurrentDictionary<Guid, Channel<bool>>? watchers))
                watchers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    // ── Processing state ────────────────────────────────────────────────────────

    public bool IsThreadProcessing(string threadKey) => processingThreads.ContainsKey(threadKey);

    // ── Attachments (proxies to Thread) ────────────────────────────────────────

    public void AddAttachment(string threadKey, Attachment attachment)
        => dialogue?.GetOrCreateThread(threadKey).AddAttachment(attachment);

    public bool RemoveAttachment(string threadKey, string name)
        => dialogue?.GetOrCreateThread(threadKey).RemoveAttachment(name) ?? false;

    public IReadOnlyList<Attachment> GetAttachments(string threadKey)
        => dialogue?.GetOrCreateThread(threadKey).GetAttachments() ?? Array.Empty<Attachment>();

    // ── Message attachments (proxies to Thread — cleared after each Prompt) ────

    public void AddMessageAttachment(string threadKey, Attachment attachment)
        => dialogue?.GetOrCreateThread(threadKey).AddMessageAttachment(attachment);

    public bool RemoveMessageAttachment(string threadKey, string name)
        => dialogue?.GetOrCreateThread(threadKey).RemoveMessageAttachment(name) ?? false;

    public IReadOnlyList<Attachment> GetMessageAttachments(string threadKey)
        => dialogue?.GetOrCreateThread(threadKey).GetMessageAttachments() ?? Array.Empty<Attachment>();

    // ── Prompting ───────────────────────────────────────────────────────────────

    public async Task<string> Prompt(string threadKey, string prompt, string username, string? platformContext = null)
    {
        if (dialogue is null)
            throw new ModelNotFoundException("Dialogue model is not loaded or is not enabled.");

        string? recallBlock = null;
        string? contextSummary = context?.GetContext(threadKey);

        if (recall is not null)
        {
            List<ThreadMessage> chatHistory = dialogue.GetThread(threadKey)?.GetChatHistory() ?? [];
            recallBlock = await recall.GetNotes(chatHistory, prompt);
        }

        processingThreads[threadKey] = 0;
        try
        {
            return await dialogue.SendPrompt(threadKey, prompt, username, platformContext, recallBlock, contextSummary);
        }
        finally
        {
            dialogue.GetOrCreateThread(threadKey).ClearMessageAttachments();
            processingThreads.TryRemove(threadKey, out _);
            // Notify watchers so they can update the isProcessing indicator.
            NotifyWatchers(threadKey);
        }
    }

    /// <summary>
    /// Passes a slash command to the CommandService for processing.
    /// If a threadKey is provided, the exchange is stored in the thread's display history.
    /// Returns a human-readable result, or null if the input is not a recognised command.
    /// </summary>
    public async Task<string?> HandleCommand(string? threadKey, string input)
    {
        string? result = await commands.Handle(input);
        if (result is not null && threadKey is not null)
            dialogue?.LogCommand(threadKey, input, result);
        return result;
    }

    public void Dispose() => engram?.Dispose();
}

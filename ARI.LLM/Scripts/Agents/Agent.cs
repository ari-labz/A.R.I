using System.Collections.Concurrent;

namespace ARI.LLM;

public class Agent
{
    public    readonly string Name;
    internal  readonly string Endpoint;
    internal  readonly string ModelString;
    internal  readonly string SystemPrompt;
    internal  readonly int    MaxTokens;
    internal  readonly int    MaxImageTokens;
    internal  readonly bool   Think;
    internal  readonly int    ThinkingBudget;
    internal  readonly int?   Slot;

    // 0 = unlimited. Overridden by agents that enforce a context window.
    internal virtual int MaxContextTokens => 0;

    // 0 = unlimited. Overridden by agents that trim short-term history.
    internal virtual int MemoryLimit => 0;

    internal virtual bool QuietLogging      => false;
    internal virtual bool SuppressPromptLog => false;

    public readonly ConcurrentDictionary<string, Thread> Threads = new();

    internal event Action<string>? ThreadUpdated;
    internal event Action<string>? ThreadDeleted;

    internal Agent(AgentConfig config)
    {
        Name           = config.Name;
        Endpoint       = config.Endpoint;
        ModelString    = config.Model;
        SystemPrompt   = config.SystemPrompt;
        MaxTokens      = config.MaxTokens;
        MaxImageTokens = config.MaxImageTokens;
        Think          = config.Think;
        ThinkingBudget = config.ThinkingBudget;
        Slot           = config.Slot;
    }

    internal IReadOnlyCollection<string> ThreadKeys => Threads.Keys.ToList().AsReadOnly();

    public Thread? GetThread(string threadKey)
        => Threads.TryGetValue(threadKey, out Thread? t) ? t : null;

    internal IEnumerable<LiveCallInfo> GetLiveCalls()
        => Threads.Values.Select(t => t.LiveCall).Where(l => l is not null)!;

    public Thread GetOrCreateThread(string threadKey)
    {
        if (!Threads.TryGetValue(threadKey, out Thread? thread))
        {
            thread = new Thread(this, threadKey);
            OnThreadCreated(threadKey, thread);
        }
        return thread;
    }

    protected Task<string> Prompt(
        string              threadKey,
        string              prompt,
        string              username               = "user",
        string?             augmentedPrompt        = null,
        string?             platformContext        = null,
        string?             recallNotes            = null,
        string?             contextSummary         = null,
        int                 maxTokensOverride      = 0,
        CancellationToken   ct                     = default,
        bool                userMessagePreadded    = false,
        Func<string, Task>? onDelta                = null,
        int                 thinkingBudgetOverride = 0)
    {
        if (!Threads.TryGetValue(threadKey, out Thread? thread))
        {
            thread = new Thread(this, threadKey, platformContext: platformContext);
            OnThreadCreated(threadKey, thread);
        }
        return thread.SendPrompt(prompt, username, augmentedPrompt, recallNotes, contextSummary, maxTokensOverride, ct, userMessagePreadded, onDelta, thinkingBudgetOverride);
    }

    protected virtual void OnThreadCreated(string threadKey, Thread thread)
    {
        Threads[threadKey] = thread;
        thread.Updated += () => ThreadUpdated?.Invoke(threadKey);
        thread.Deleted += () =>
        {
            Threads.TryRemove(threadKey, out _);
            ThreadDeleted?.Invoke(threadKey);
        };
    }
}

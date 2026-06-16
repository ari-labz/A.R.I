using System.Collections.Concurrent;

namespace ARI.LLM;

public abstract class Agent
{
    public    readonly string Name;
    public    readonly string ServerName;
    internal  readonly string Endpoint;
    internal  readonly string SystemPrompt;
    internal  readonly int    MaxTokens;
    internal  readonly int    MaxImageTokens;
    internal  readonly bool   Think;
    internal  readonly int    ThinkingBudget;
    internal  readonly int?   Slot;
    internal  readonly double? Temperature;
    internal  readonly double? TopP;
    internal  readonly int?    TopK;
    internal  readonly double? RepeatPenalty;
    internal  readonly double? PresencePenalty;
    internal  readonly double? FrequencyPenalty;
    internal  readonly int     MaxToolCalls;

    // 0 = unlimited. Overridden by agents that enforce a context window.
    internal virtual int MaxContextTokens => 0;

    // 0 = unlimited. Overridden by agents that trim short-term history.
    internal virtual int MemoryLimit => 0;

    internal virtual bool QuietLogging      => false;
    internal virtual bool SuppressPromptLog => false;

    /// <summary>The pipeline this agent runs. Every thread it owns is tagged with this type.</summary>
    internal abstract ThreadType Type { get; }

    // The service owns one shared registry of every thread; agents hold a reference to it.
    // A standalone default is kept so an agent works before the service attaches the shared one.
    private ConcurrentDictionary<string, Thread> registry = new();

    internal void AttachRegistry(ConcurrentDictionary<string, Thread> shared) => registry = shared;

    /// <summary>This agent's own threads — a type-filtered view over the shared registry.</summary>
    public IReadOnlyDictionary<string, Thread> Threads =>
        registry.Where(kv => kv.Value.Type == Type).ToDictionary(kv => kv.Key, kv => kv.Value);

    internal event Action<string>? ThreadUpdated;
    internal event Action<string>? ThreadDeleted;

    internal Agent(AgentConfig config)
    {
        Name       = config.Name;
        ServerName = config.ServerName;
        Endpoint   = config.Endpoint;
        SystemPrompt   = config.SystemPrompt;
        MaxTokens      = config.MaxTokens;
        MaxImageTokens = config.MaxImageTokens;
        Think          = config.Think;
        ThinkingBudget = config.ThinkingBudget;
        Slot           = config.Slot;
        Temperature      = config.Temperature;
        TopP             = config.TopP;
        TopK             = config.TopK;
        RepeatPenalty    = config.RepeatPenalty;
        PresencePenalty  = config.PresencePenalty;
        FrequencyPenalty = config.FrequencyPenalty;
        MaxToolCalls   = config.MaxToolCalls;
    }

    /// <summary>This agent's own threads as live objects (unfiltered enumeration of the type view).</summary>
    internal IEnumerable<Thread> OwnThreads => Threads.Values;

    public Thread? GetThread(string threadKey)
        => registry.TryGetValue(threadKey, out Thread? t) && t.Type == Type ? t : null;

    protected void RemoveThread(string threadKey) => registry.TryRemove(threadKey, out _);

    // ── Command logging ──────────────────────────────────────────────────────────
    // A command's input is shown immediately to acknowledge it; its response follows
    // once the command finishes (which can be minutes later).

    internal void AddCommandInput(string threadKey, string input)
    {
        if (GetThread(threadKey) is { } t) t.AddItem(new CommandInput { Input = input, Timestamp = DateTime.Now });
    }

    internal void AddCommandResponse(string threadKey, string response)
    {
        if (GetThread(threadKey) is { } t) t.AddItem(new CommandResponse { Response = response, Timestamp = DateTime.Now });
    }

    internal void DropCommandInput(string threadKey)
    {
        if (GetThread(threadKey) is { } t) t.DropLastCommandInput();
    }

    public Thread GetOrCreateThread(string threadKey)
    {
        Thread? thread = GetThread(threadKey);
        if (thread is null)
        {
            thread = new Thread(Type, threadKey);
            OnThreadCreated(threadKey, thread);
        }
        return thread;
    }

    private const int CHARS_PER_TOKEN = 4;

    /// <summary>The slice of a thread's history this agent would send as context.</summary>
    internal List<ThreadMessage> ContextSnapshot(Thread thread)
    {
        int maxChars = MaxContextTokens > 0 ? MaxContextTokens * 2 : 0;
        return thread.GetChatHistory(MemoryLimit, maxChars);
    }

    public (int Used, int Limit) GetContextStats(Thread? thread)
    {
        if (thread is null) return (0, MaxContextTokens);
        List<ThreadMessage> ctx = ContextSnapshot(thread);
        int chars               = ctx.Sum(m => (m.Username?.Length ?? 0) + 2 + (m.Content?.Length ?? 0));
        return (chars / CHARS_PER_TOKEN, MaxContextTokens);
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
        Thread? thread = GetThread(threadKey);
        if (thread is null)
        {
            thread = new Thread(Type, threadKey, platformContext: platformContext);
            OnThreadCreated(threadKey, thread);
        }
        return thread.SendPrompt(this, prompt, username, augmentedPrompt, recallNotes, contextSummary, maxTokensOverride, ct, userMessagePreadded, onDelta, thinkingBudgetOverride);
    }

    protected virtual void OnThreadCreated(string threadKey, Thread thread)
    {
        registry[threadKey] = thread;
        thread.Updated += () => ThreadUpdated?.Invoke(threadKey);
        thread.Deleted += () =>
        {
            registry.TryRemove(threadKey, out _);
            ThreadDeleted?.Invoke(threadKey);
        };
    }
}

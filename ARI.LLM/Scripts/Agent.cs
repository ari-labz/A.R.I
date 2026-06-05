namespace ARI.LLM;

internal class Agent
{
    internal readonly string Name;
    internal readonly string Endpoint;
    internal readonly string ModelString;
    internal readonly string SystemPrompt;
    internal readonly int    MaxTokens;
    internal readonly bool   Think;

    protected readonly Dictionary<string, Thread> threads = new();

    /// <summary>Fires whenever any thread's history changes. Payload is the thread key.</summary>
    internal event Action<string>? ThreadUpdated;

    /// <summary>Fires when a thread is deleted after its dormant period. Payload is the thread key.</summary>
    internal event Action<string>? ThreadDeleted;

    protected Agent(AgentConfig config)
    {
        Name         = config.Name;
        Endpoint     = config.Endpoint;
        ModelString  = config.Model;
        SystemPrompt = config.SystemPrompt;
        MaxTokens    = config.MaxTokens;
        Think        = config.Think;
    }

    // ── Threads ─────────────────────────────────────────────────────────────────

    internal IReadOnlyCollection<string> ThreadKeys => threads.Keys.ToList().AsReadOnly();

    internal Thread? GetThread(string threadKey)
        => threads.TryGetValue(threadKey, out Thread? t) ? t : null;

    internal Thread GetOrCreateThread(string threadKey)
    {
        if (!threads.TryGetValue(threadKey, out Thread? thread))
        {
            thread = new Thread(this, threadKey, shortTermMemoryLimit: GetShortTermMemoryLimit(), maxContextTokens: GetMaxContextTokens());
            OnThreadCreated(threadKey, thread);
        }
        return thread;
    }

    // ── Prompting ───────────────────────────────────────────────────────────────

    protected Task<string> Prompt(
        string               threadKey,
        string               prompt,
        string               username             = "user",
        string?              augmentedPrompt      = null,
        string?              platformContext      = null,
        string?              recallNotes          = null,
        string?              contextSummary       = null,
        int                  maxTokensOverride    = 0,
        CancellationToken    ct                   = default,
        bool                 userMessagePreadded  = false,
        Func<string, Task>?  onDelta              = null)
    {
        if (!threads.TryGetValue(threadKey, out Thread? thread))
        {
            thread = new Thread(this, threadKey, platformContext: platformContext, shortTermMemoryLimit: GetShortTermMemoryLimit(), maxContextTokens: GetMaxContextTokens());
            OnThreadCreated(threadKey, thread);
        }
        return thread.SendPrompt(prompt, username, augmentedPrompt, recallNotes, contextSummary, maxTokensOverride, ct, userMessagePreadded, onDelta);
    }

    // ── Overridable ─────────────────────────────────────────────────────────────

    protected virtual int  GetShortTermMemoryLimit() => 0;
    protected virtual int  GetMaxContextTokens()      => 0;

    /// <summary>
    /// When true, Thread suppresses all per-call log lines (prompt, response, timing).
    /// Used by agents that emit their own higher-level log summaries (e.g. Memory).
    /// </summary>
    internal virtual bool QuietLogging => false;

    /// <summary>
    /// When true, Thread suppresses only the prompt log line.
    /// Used when the caller logs the prompt earlier in the pipeline (e.g. Dialogue, which
    /// logs before the Memory pre-flight so the prompt appears first in the output).
    /// </summary>
    internal virtual bool SuppressPromptLog => false;

    /// <summary>
    /// Called when a new thread is created. Registers the thread and subscribes to its update event.
    /// Subclasses should call base.OnThreadCreated before adding their own subscriptions.
    /// </summary>
    protected virtual void OnThreadCreated(string threadKey, Thread thread)
    {
        threads[threadKey] = thread;
        thread.Updated  += () => ThreadUpdated?.Invoke(threadKey);
        thread.Deleted  += () =>
        {
            threads.Remove(threadKey);
            ThreadDeleted?.Invoke(threadKey);
        };
    }
}

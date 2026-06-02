namespace ARI.LLM;

internal class Agent
{
    internal readonly string  Name;
    internal readonly string  Endpoint;
    internal readonly string  ModelString;
    internal readonly string  SystemPrompt;
    internal readonly int     ShortTermMemoryLimit;
    internal readonly int     MaxContextTokens;
    internal readonly int     MaxTokens;
    internal readonly string? ExtractionPrompt;

    private readonly Dictionary<string, Thread> threads = new();

    /// <summary>Fires whenever a thread's history changes. Payload is the thread key.</summary>
    internal event Action<string>? ThreadHistoryUpdated;

    protected Agent(ModelConfig config)
    {
        Name                 = config.Name;
        Endpoint             = config.Endpoint;
        ModelString          = config.Model;
        SystemPrompt         = config.SystemPrompt;
        ShortTermMemoryLimit = config.ShortTermMemoryLimit;
        MaxContextTokens     = config.MaxContextTokens;
        MaxTokens            = config.MaxTokens;
        ExtractionPrompt     = config.ExtractionPrompt;
    }

    // ── Thread accessors ────────────────────────────────────────────────────────

    internal IReadOnlyCollection<string> ThreadKeys => threads.Keys.ToList().AsReadOnly();

    internal IReadOnlyList<ThreadItem> GetThreadItems(string threadKey)
        => threads.TryGetValue(threadKey, out Thread? t) ? t.GetThreadHistory() : Array.Empty<ThreadItem>();

    internal DateTime GetThreadLastMessageAt(string threadKey)
        => threads.TryGetValue(threadKey, out Thread? t) ? t.LastMessageAt : DateTime.MinValue;

    // ── ThreadItem injection ────────────────────────────────────────────────────

    internal void InjectCommandExchange(string threadKey, string input, string response)
    {
        if (threads.TryGetValue(threadKey, out Thread? t))
            t.AddItem(new CommandExchange { Input = input, Response = response, Timestamp = DateTime.Now });
    }

    internal void InjectEngramEvent(string threadKey, IReadOnlyList<NoteChange> changes)
    {
        if (threads.TryGetValue(threadKey, out Thread? t))
            t.AddItem(new EngramEvent { Changes = changes, Timestamp = DateTime.Now });
    }

    // ── Prompting ───────────────────────────────────────────────────────────────

    protected Task<string> PromptThread(
        string  threadKey,
        string  prompt,
        string  username          = "user",
        string? augmentedPrompt   = null,
        string? contextNote       = null,
        string? recallNotes       = null,
        string? contextSummary    = null,
        int     maxTokensOverride = 0)
    {
        if (!threads.TryGetValue(threadKey, out Thread? thread))
        {
            thread = new Thread(this, threadKey, contextNote);
            thread.HistoryUpdated += () => ThreadHistoryUpdated?.Invoke(threadKey);
            OnThreadCreated(threadKey, thread);
            threads[threadKey] = thread;
        }
        return thread.SendPrompt(prompt, username, augmentedPrompt, recallNotes, contextSummary, maxTokensOverride);
    }

    /// <summary>
    /// Returns a snapshot of the current LLM context window as ChatMessages.
    /// Used by Engram to seed ad-hoc write threads.
    /// </summary>
    protected IReadOnlyList<ChatMessage> GetThreadSnapshot(string threadKey)
        => threads.TryGetValue(threadKey, out Thread? t) ? t.GetSnapshotForAdHoc() : Array.Empty<ChatMessage>();

    /// <summary>
    /// Sends a single prompt to an ephemeral ad-hoc thread seeded from a context snapshot.
    /// The thread is not stored — used only for Engram's per-note write calls.
    /// </summary>
    protected Task<string> PromptAdHocThread(IReadOnlyList<ChatMessage> seedMessages, string prompt, int maxTokensOverride = 0)
    {
        Thread thread = new Thread(this, $"adhoc:{Guid.NewGuid()}", seedMessages);
        return thread.SendPrompt(prompt, maxTokensOverride: maxTokensOverride);
    }

    protected virtual void OnThreadCreated(string threadKey, Thread thread) { }
}

using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace ARI.LLM;

public abstract class Agent
{
    // ── JSON-serialised fields (common to all agents) ────────────────────────
    [JsonPropertyName("name")]          public string  Name          { get; init; } = "";
    [JsonPropertyName("serverName")]    public string  ServerName    { get; set; }  = "";
    [JsonPropertyName("systemPrompt")]  public string  SystemPrompt  { get; init; } = "";
    [JsonPropertyName("enabled")]       public bool    Enabled       { get; init; }
    [JsonPropertyName("maxTokens")]     public int     MaxTokens     { get; init; } = -1;
    [JsonPropertyName("maxToolCalls")]  public int     MaxToolCalls  { get; init; }
    [JsonPropertyName("think")]         public bool    Think         { get; init; }
    [JsonPropertyName("thinkingBudget")]public int     ThinkingBudget{ get; init; }
    [JsonPropertyName("slot")]          public int?    Slot          { get; set; }
    [JsonPropertyName("temperature")]   public double? Temperature   { get; init; }
    [JsonPropertyName("topP")]          public double? TopP          { get; init; }
    [JsonPropertyName("topK")]          public int?    TopK          { get; init; }
    [JsonPropertyName("repeatPenalty")] public double? RepeatPenalty { get; init; }
    [JsonPropertyName("presencePenalty")]  public double? PresencePenalty  { get; init; }
    [JsonPropertyName("frequencyPenalty")]  public double? FrequencyPenalty  { get; init; }
    [JsonPropertyName("maxContextTokens")] public int     MaxContextTokens  { get; init; }

    // ── Runtime-only ─────────────────────────────────────────────────────────
    [JsonIgnore] public string Endpoint { get; internal set; } = "";

    // 0 = unlimited. Overridden by agents that trim short-term history.
    [JsonIgnore] internal virtual int  MemoryLimit => 0;

    [JsonIgnore] internal virtual bool QuietLogging      => false;
    [JsonIgnore] internal virtual bool SuppressPromptLog => false;

    internal abstract ThreadType Type { get; }

    private ConcurrentDictionary<string, Thread> registry = new();

    internal void AttachRegistry(ConcurrentDictionary<string, Thread> shared) => registry = shared;

    public IReadOnlyDictionary<string, Thread> Threads =>
        registry.Where(kv => kv.Value.Type == Type).ToDictionary(kv => kv.Key, kv => kv.Value);

    internal event Action<string>? ThreadUpdated;
    internal event Action<string>? ThreadDeleted;

    internal IEnumerable<Thread> OwnThreads => Threads.Values;

    public Thread? GetThread(string threadKey)
        => registry.TryGetValue(threadKey, out Thread? t) && t.Type == Type ? t : null;

    protected void RemoveThread(string threadKey) => registry.TryRemove(threadKey, out _);

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

    internal List<ThreadMessage> ContextSnapshot(Thread thread)
    {
        int maxChars = MaxContextTokens > 0 ? MaxContextTokens * 2 : 0;
        return thread.GetChatHistory(MemoryLimit, maxChars);
    }

    public (int Used, int Limit) GetContextStats(Thread? thread)
    {
        if (thread is null) return (0, MaxContextTokens);
        List<ThreadMessage> ctx = ContextSnapshot(thread);
        int chars = ctx.Sum(m => (m.Username?.Length ?? 0) + 2 + (m.Content?.Length ?? 0));
        return (chars / CHARS_PER_TOKEN, MaxContextTokens);
    }

    protected Task<string> Prompt(
        string threadKey,
        string prompt,
        string username = "user",
        string? augmentedPrompt = null,
        string? platformContext = null,
        string? recallNotes = null,
        string? contextSummary = null,
        int maxTokensOverride = 0,
        CancellationToken ct = default,
        bool userMessagePreadded = false,
        Func<string, Task>? onDelta = null,
        int thinkingBudgetOverride = 0)
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

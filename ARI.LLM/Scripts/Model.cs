namespace ARI.LLM;

internal class Model
{
    internal readonly string Name;
    internal readonly string Endpoint;
    internal readonly string ModelString;
    internal readonly string SystemPrompt;
    internal readonly int ShortTermMemoryLimit;
    internal readonly int MaxTokens;
    internal readonly string? ExtractionPrompt;

    private readonly Dictionary<string, Thread> threads = new();

    protected Model(ModelConfig config)
    {
        Name                 = config.Name;
        Endpoint             = config.Endpoint;
        ModelString          = config.Model;
        SystemPrompt         = config.SystemPrompt;
        ShortTermMemoryLimit = config.ShortTermMemoryLimit;
        MaxTokens            = config.MaxTokens;
        ExtractionPrompt     = config.ExtractionPrompt;
    }

    internal IReadOnlyCollection<string> ThreadKeys => threads.Keys.ToList().AsReadOnly();

    internal IReadOnlyList<ChatMessage> GetThreadHistory(string threadKey)
        => threads.TryGetValue(threadKey, out Thread? t) ? t.GetHistory() : Array.Empty<ChatMessage>();

    internal IReadOnlyList<ChatMessage> GetThreadDisplayHistory(string threadKey)
        => threads.TryGetValue(threadKey, out Thread? t) ? t.GetDisplayHistory() : Array.Empty<ChatMessage>();

    internal DateTime GetThreadLastMessageAt(string threadKey)
        => threads.TryGetValue(threadKey, out Thread? t) ? t.LastMessageAt : DateTime.MinValue;

    protected Task<string> PromptThread(string threadKey, string prompt, string? contextNote = null, string? originalUserMessage = null, string? recallNotes = null, string? contextSummary = null)
    {
        if (!threads.TryGetValue(threadKey, out Thread? thread))
        {
            thread = new Thread(this, threadKey, contextNote);
            OnThreadCreated(threadKey, thread);
            threads[threadKey] = thread;
        }
        return thread.SendPrompt(prompt, originalUserMessage, recallNotes, contextSummary);
    }

    protected virtual void OnThreadCreated(string threadKey, Thread thread) { }
}

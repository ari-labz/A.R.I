namespace ARI.LLM;

internal class Model
{
    internal readonly string Name;
    internal readonly string Endpoint;
    internal readonly string ModelString;
    internal readonly string SystemPrompt;
    internal readonly int ShortTermMemoryLimit;
    internal readonly int MaxTokens;

    private readonly Dictionary<string, Thread> threads;

    internal Model(ModelConfig config)
    {
        Name = config.Name;
        Endpoint = config.Endpoint;
        ModelString = config.Model;
        SystemPrompt = config.SystemPrompt;
        ShortTermMemoryLimit = config.ShortTermMemoryLimit;
        MaxTokens = config.MaxTokens;
        threads = new Dictionary<string, Thread>();
    }

    internal event Action<string, IReadOnlyList<ChatMessage>>? ThreadBufferFull;
    internal event Action<string, string, string>? ThreadExchangeCompleted; // (threadKey, userMessage, assistantResponse)

    internal Task<string> SendPrompt(string threadKey, string prompt, string? contextNote = null)
    {
        if (!threads.TryGetValue(threadKey, out Thread? thread))
        {
            thread = new Thread(this, threadKey, contextNote);
            thread.BufferFull           += history           => ThreadBufferFull?.Invoke(threadKey, history);
            thread.ExchangeCompleted    += (user, assistant) => ThreadExchangeCompleted?.Invoke(threadKey, user, assistant);
            threads[threadKey] = thread;
        }

        return thread.SendPrompt(prompt);
    }

    internal IReadOnlyList<ChatMessage> GetThreadHistory(string threadKey)
    {
        return threads.TryGetValue(threadKey, out Thread? thread)
            ? thread.GetHistory()
            : Array.Empty<ChatMessage>();
    }

    internal DateTime GetThreadLastMessageAt(string threadKey)
    {
        return threads.TryGetValue(threadKey, out Thread? thread)
            ? thread.LastMessageAt
            : DateTime.MinValue;
    }

    internal IReadOnlyCollection<string> ThreadKeys => threads.Keys.ToList().AsReadOnly();
}

namespace ARI.LLM;

internal class Model
{
    internal readonly string Endpoint;
    internal readonly string ModelString;
    internal readonly string SystemPrompt;
    internal readonly int ShortTermMemoryLimit;

    private readonly Dictionary<string, Thread> threads;

    internal Model(ModelConfig config)
    {
        Endpoint = config.Endpoint;
        ModelString = config.Model;
        SystemPrompt = config.SystemPrompt;
        ShortTermMemoryLimit = config.ShortTermMemoryLimit;
        threads = new Dictionary<string, Thread>();
    }

    internal event Action<string, IReadOnlyList<ChatMessage>>? ThreadBufferFull;

    internal Task<string> SendPrompt(string threadKey, string prompt, string? contextNote = null)
    {
        if (!threads.TryGetValue(threadKey, out Thread? thread))
        {
            thread = new Thread(this, contextNote);
            thread.BufferFull += history => ThreadBufferFull?.Invoke(threadKey, history);
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

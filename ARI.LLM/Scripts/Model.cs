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

    internal Task<string> SendPrompt(string threadKey, string prompt, string? contextNote = null)
    {
        if (!threads.TryGetValue(threadKey, out Thread? thread))
        {
            thread = new Thread(this, contextNote);
            threads[threadKey] = thread;
        }

        return thread.SendPrompt(prompt);
    }
}

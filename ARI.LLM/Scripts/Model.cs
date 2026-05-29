namespace ARI.LLM;

internal class Model
{
    internal readonly string Endpoint;
    internal readonly string ModelString;
    internal readonly string SystemPrompt;
    internal readonly int HistoryLimit;

    private readonly Dictionary<string, Thread> threads;

    internal Model(ModelConfig config)
    {
        Endpoint = config.Endpoint;
        ModelString = config.Model;
        SystemPrompt = config.SystemPrompt;
        HistoryLimit = config.HistoryLimit;
        threads = new Dictionary<string, Thread>();
    }

    internal Task<string> SendPrompt(string threadKey, string prompt)
    {
        if (!threads.TryGetValue(threadKey, out Thread? thread))
        {
            thread = new Thread(this);
            threads[threadKey] = thread;
        }

        return thread.SendPrompt(prompt);
    }
}

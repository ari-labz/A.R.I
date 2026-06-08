namespace ARI.LLM;

internal class Classifier : Agent
{
    internal Classifier(AgentConfig config) : base(config) { }

    internal async Task<string> Classify(string message, CancellationToken ct = default)
    {
        string ephemeralKey = $"__classify_{Guid.NewGuid():N}";
        try
        {
            string result = await Prompt(ephemeralKey, message, ct: ct);
            return result.Trim().StartsWith("CODE", StringComparison.OrdinalIgnoreCase) ? "Code" : "Dialogue";
        }
        finally
        {
            Threads.TryRemove(ephemeralKey, out _);
        }
    }
}

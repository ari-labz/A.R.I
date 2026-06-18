namespace ARI.LLM;

internal class Classifier : Agent
{
    internal Classifier() { }

    internal override ThreadType Type => ThreadType.Classifier;

    internal async Task<string> Classify(string message, CancellationToken ct = default)
    {
        string ephemeralKey = $"__classify_{Guid.NewGuid():N}";
        try
        {
            string result = await SendPrompt(ephemeralKey, message, ct: ct);
            return result.Trim().StartsWith("CODE", StringComparison.OrdinalIgnoreCase) ? "Code" : "Dialogue";
        }
        finally
        {
            RemoveThread(ephemeralKey);
        }
    }
}

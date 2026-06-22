namespace ARI.LLM;

internal class Classifier : Agent
{
    public Classifier() { }

    internal async Task<string> Classify(string message, CancellationToken ct = default)
    {
        Thread ephemeral = new Thread(ThreadPipeline.Dialogue, $"__classify_{Guid.NewGuid():N}") { Internal = true };
        string result    = await SendPrompt(ephemeral, message, ct: ct);
        return result.Trim().StartsWith("CODE", StringComparison.OrdinalIgnoreCase) ? "Code" : "Dialogue";
    }
}

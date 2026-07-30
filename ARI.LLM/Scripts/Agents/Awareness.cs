namespace ARI.LLM;

/// <summary>
/// Fast "conversational awareness" gate for the Speech pipeline: given a voice transcript, decides whether
/// Ari is being addressed or is merely overhearing background talk. One ephemeral, Internal, no-thinking
/// call with a tiny token budget, tuned to be as low-latency as possible.
/// </summary>
internal class Awareness : Agent
{
    public Awareness() { }

    internal override bool SuppressLog() => true;

    /// <summary>Returns true if the transcript appears to be addressed to Ari.</summary>
    internal async Task<bool> IsAddressed(string transcript, string? context = null, CancellationToken ct = default)
    {
        Thread ephemeral = new Thread(ThreadPipeline.Dialogue, $"__aware_{Guid.NewGuid():N}") { Internal = true };
        string prompt = string.IsNullOrEmpty(context)
            ? $"Transcript: \"{transcript}\""
            : $"{context}\nTranscript: \"{transcript}\"";
        // Force a tiny budget regardless of JSON config — the answer is a single word.
        string result = await Prompt(ephemeral, prompt, new PromptOptions { MaxTokensOverride = 8, Ct = ct });
        return result.Trim().StartsWith("ADDRESSED", StringComparison.OrdinalIgnoreCase);
    }
}

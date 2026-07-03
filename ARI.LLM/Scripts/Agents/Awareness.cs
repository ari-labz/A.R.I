namespace ARI.LLM;

/// <summary>
/// Fast "conversational awareness" gate for the Speech pipeline: given a voice transcript, decides whether
/// Ari is being addressed or is merely overhearing background talk. Mirrors <see cref="Classifier"/> — one
/// ephemeral, Internal, no-thinking call with a tiny token budget, tuned to be as low-latency as possible.
/// </summary>
internal class Awareness : Agent
{
    public Awareness() { }

    // Baked-in default so the gate works even without an Agents.json entry (JSON systemPrompt overrides).
    internal const string DefaultSystemPrompt =
        "You are Ari. You are given a short transcript of speech that was just heard. Decide whether the " +
        "speaker is addressing you directly, or whether it is background talk / cross-conversation you are " +
        "merely overhearing. Reply with ONLY one word — ADDRESSED or OVERHEARD — and nothing else.";

    internal override bool QuietLogging => true;

    /// <summary>Returns true if the transcript appears to be addressed to Ari.</summary>
    internal async Task<bool> IsAddressed(string transcript, string? context = null, CancellationToken ct = default)
    {
        Thread ephemeral = new Thread(ThreadPipeline.Dialogue, $"__aware_{Guid.NewGuid():N}") { Internal = true };
        string prompt = string.IsNullOrEmpty(context)
            ? $"Transcript: \"{transcript}\""
            : $"{context}\nTranscript: \"{transcript}\"";
        // Force a tiny budget regardless of JSON config — the answer is a single word.
        string result = await SendPrompt(ephemeral, prompt, maxTokensOverride: 8, ct: ct);
        return result.Trim().StartsWith("ADDRESSED", StringComparison.OrdinalIgnoreCase);
    }
}

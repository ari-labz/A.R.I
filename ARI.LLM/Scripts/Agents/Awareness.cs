using System.Text;

namespace ARI.LLM;

/// <summary>
/// Fast "conversational awareness" gate: decides whether Ari is being addressed.
/// Works for both voice transcripts and text chat messages. Ephemeral, no-thinking,
/// tiny token budget — tuned for low latency on the Utility Server.
/// </summary>
internal class Awareness : Agent
{
    public Awareness() { }

    internal override bool SuppressLog => true;

    // Speech is not a group chat: a microphone in the room also hears phone calls, other people and the TV. Lines with
    // her name, or replies right after she spoke, never reach this gate (the listener answers those itself).
    private const string VoiceFraming =
        "This is speech a microphone in the room picked up, not a chat message. It did not say Ari's name and did not " +
        "come right after she spoke. Someone on the phone, talking to another person, or a TV or radio is OVERHEARD. " +
        "Only say ADDRESSED if it is plainly a request or question meant for an assistant.";

    /// <summary>Voice gate: is this spoken transcript addressed to Ari?</summary>
    internal async Task<bool> IsAddressed(string transcript, string? context = null, CancellationToken ct = default)
    {
        Thread ephemeral = new Thread(ThreadPipeline.Dialogue, $"__aware_{Guid.NewGuid():N}") { Internal = true };
        string prompt = string.IsNullOrEmpty(context)
            ? $"{VoiceFraming}\nTranscript: \"{transcript}\""
            : $"{VoiceFraming}\n{context}\nTranscript: \"{transcript}\"";
        string result = await Prompt(ephemeral, prompt, new PromptOptions { MaxTokensOverride = 8, Ct = ct });
        return result.Trim().StartsWith("ADDRESSED", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Text gate: given recent chat history and the latest message, should Ari respond?</summary>
    internal async Task<bool> ShouldRespond(IReadOnlyList<ThreadMessage> recentMessages, string latestMessage, CancellationToken ct = default)
    {
        Thread ephemeral = new Thread(ThreadPipeline.Dialogue, $"__aware_{Guid.NewGuid():N}") { Internal = true };

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("Recent chat:");
        foreach (ThreadMessage msg in recentMessages)
            sb.AppendLine($"  {msg.Username}: {msg.Content}");
        sb.AppendLine();
        sb.AppendLine($"Latest message: {latestMessage}");

        string result = await Prompt(ephemeral, sb.ToString(), new PromptOptions { MaxTokensOverride = 8, Ct = ct });
        return result.Trim().StartsWith("ADDRESSED", StringComparison.OrdinalIgnoreCase);
    }
}

namespace ARI.LLM;

// Speech-turn agent owned by SpeechPipeline. Shares a Thread (and thus the llama-server KV slot)
// with TextingAgent so the context prefix is reused between text and speech turns.
// All speech-specific logic (steering, redirect) lives here and in SpeechPipeline — never on the
// base Dialogue class or on Thread.
internal sealed class TalkingAgent : Dialogue
{
    // Set by SpeechPipeline before Prompt(), cleared in a finally block after.
    internal SpeechSteeringContext? Steering { get; set; }

    internal override string? OnStreamingDelta(Thread thread, string delta)
    {
        if (Steering is null || !Steering.UserStillTalking) return null;
        return Steering.ConsumeAll();
    }
}

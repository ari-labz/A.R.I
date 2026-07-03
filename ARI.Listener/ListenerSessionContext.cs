namespace ARI.Listener;

/// <summary>Who/where an audio stream is coming from, carried through the pipeline.</summary>
public sealed record ListenerSessionContext(
    string  Source,          // "web" | "client" | "discord"
    string  ThreadKey,
    string? UserId    = null,
    string? ChannelId = null);

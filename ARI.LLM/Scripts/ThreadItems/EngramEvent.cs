namespace ARI.LLM;

/// <summary>
/// Records what Engram stored to the brain during a sweep.
/// Rendered as "ARI will remember this" in the client.
/// Never contributes to LLM context (Message is null).
/// </summary>
public class EngramEvent : ThreadItem
{
    public required IReadOnlyList<NoteChange> Changes { get; init; }
    // Message intentionally null — memory events are UI-only.

    public override string ToString()
    {
        var parts = Changes.Select(c => c.Op == "created"
            ? $"added \"{c.Title}\""
            : $"updated \"{c.Title}\"");
        return $"[{Timestamp:HH:mm}] [Memory] {string.Join(", ", parts)}";
    }
}

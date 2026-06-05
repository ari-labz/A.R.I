namespace ARI.LLM;

/// <summary>
/// A slash-command and its result, shown as a paired UI element.
/// Never contributes to LLM context (Message is null).
/// </summary>
public class CommandExchange : ThreadItem
{
    public required string Input    { get; init; }
    public required string Response { get; init; }
    // Message intentionally null — commands are UI-only, LLMs never see them.

    public override string ToString() =>
        $"[{Timestamp:HH:mm}] /{Input} → {Response}";
}

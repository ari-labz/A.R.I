namespace ARI.LLM;

/// <summary>
/// The result of a slash-command, added once it finishes (which may be long after the
/// matching <see cref="CommandInput"/>). Never contributes to LLM context (Message is null).
/// </summary>
public class CommandResponse : ThreadItem
{
    public required string Response { get; init; }

    public override string ToString() => $"[{Timestamp:HH:mm}] {Response}";
}

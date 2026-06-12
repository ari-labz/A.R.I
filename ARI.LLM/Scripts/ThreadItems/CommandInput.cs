namespace ARI.LLM;

/// <summary>
/// A slash-command as entered by the user, shown immediately to acknowledge it was accepted.
/// The result arrives separately as a <see cref="CommandResponse"/>, since some commands run for minutes.
/// Never contributes to LLM context (Message is null).
/// </summary>
public class CommandInput : ThreadItem
{
    public required string Input { get; init; }

    public override string ToString() => $"[{Timestamp:HH:mm}] {Input}";
}

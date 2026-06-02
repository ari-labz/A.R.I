namespace ARI.LLM;

/// <summary>A message sent by a human user.</summary>
public class UserMessage : ThreadItem
{
    public required string Username { get; init; }
    public required string Content  { get; init; }

    public override string? Message    => Content;
    public override string  AuthorName => Username;
}

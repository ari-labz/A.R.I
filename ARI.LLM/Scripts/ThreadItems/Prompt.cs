using System.Text.Json.Serialization;

namespace ARI.LLM;

/// <summary>A message sent by a human user.</summary>
public class Prompt : ThreadItem
{
    // Serialised as "content" + "username" to match the original UserMessage wire shape the client reads.
    [JsonPropertyName("content")]
    public required string Text { get; init; }

    [JsonPropertyName("username")]
    public string Username => AuthorName;

    /// <summary>Files attached to this specific message. Null when no attachments were sent.</summary>
    public List<Attachment>? Attachments { get; init; }

    [JsonIgnore]
    public override string? Message => Text;

    public override string ToString() =>
        $"[{Timestamp:HH:mm}] {AuthorName}: {Text}";
}

/// <summary>A prompt the framework issues to itself / the model — internal orchestration
/// (e.g. the architect's fix or summary instructions). Not shown in the chat UI, but it does
/// contribute to the LLM context. Replaces the old ChatHidden Prompt pattern.</summary>
public class InternalPrompt : ThreadItem
{
    public required string Text { get; init; }

    [JsonIgnore]
    public override string? Message => Text;

    public override string ToString() =>
        $"[{Timestamp:HH:mm}] [internal] {Text}";
}

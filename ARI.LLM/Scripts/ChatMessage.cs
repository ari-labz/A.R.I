using System.Text.Json.Serialization;

namespace ARI.LLM;

/// <summary>
/// Wire-format message for LLM API requests.
/// Used only by ad-hoc Engram write threads — all display history uses ThreadItem instead.
/// </summary>
public class ChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

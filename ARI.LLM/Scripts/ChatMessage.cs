using System.Text.Json.Serialization;

namespace ARI.LLM;

internal class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; }

    [JsonPropertyName("content")]
    public string Content { get; init; }
}

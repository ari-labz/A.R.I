using System.Text.Json.Serialization;

namespace ARI.LLM;

public class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; }

    [JsonPropertyName("content")]
    public string Content { get; init; }

    [JsonIgnore]
    public DateTime? Timestamp { get; init; }

    [JsonIgnore]
    public double? ThinkingSeconds { get; init; }

    [JsonIgnore]
    public string? RecallNotes { get; init; }

    [JsonIgnore]
    public string? ContextSummary { get; init; }
}

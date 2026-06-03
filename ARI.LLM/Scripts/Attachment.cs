using System.Text.Json.Serialization;

namespace ARI.LLM;

/// <summary>
/// A file attached to either a thread (persistent) or a message (ephemeral).
/// Owner determines scope — Thread.Attachments vs UserMessage.Attachments.
/// </summary>
public class Attachment
{
    public required string  Name     { get; init; }
    public required bool    IsImage  { get; init; }
    public string?          MimeType { get; init; }

    /// <summary>Base64 for images; UTF-8 text for documents. Excluded from history JSON — served via dedicated endpoint.</summary>
    [JsonIgnore]
    public required string Content { get; init; }
}

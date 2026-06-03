using System.Text.Json.Serialization;

namespace ARI.LLM;

/// <summary>A file attached to a single message. Cleared from the server after the message is sent.</summary>
public record MessageAttachmentInfo(
    string  Name,
    // Content is excluded from history JSON — served via /api/threads/{key}/msg-attachment endpoint instead.
    [property: JsonIgnore] string Content,
    bool    IsImage,
    string? MimeType = null);

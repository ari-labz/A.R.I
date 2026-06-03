namespace ARI.LLM;

/// <summary>A file attached to a dialogue thread. Persists for the lifetime of the thread.</summary>
public record ThreadAttachment(
    string  Name,
    string  Content,   // plain text for text files; base64-encoded for images
    bool    IsImage,
    string? MimeType = null);

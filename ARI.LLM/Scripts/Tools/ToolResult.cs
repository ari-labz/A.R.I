namespace ARI.LLM;

/// <summary>What a tool's Execute produced, and the tag that tells the caller where it goes. Text is
/// injected into the model's context; an image is routed to the vision path (or, when vision is off,
/// rendered back to text by the router). The Kind carries that decision out of the tool so the tool
/// itself never has to know how the model consumes content. A bare string a tool returns becomes a
/// Text result through the implicit conversion, so text tools stay unchanged.</summary>
public readonly struct ToolResult
{
    internal enum ContentKind { Text, Image }

    internal ContentKind Kind      { get; }
    internal string      Text      { get; }   // set when Kind is Text
    internal byte[]      Bytes     { get; }   // set when Kind is Image
    internal string      MediaType { get; }   // e.g. "image/png"; empty for text

    private ToolResult(ContentKind kind, string text, byte[] bytes, string mediaType)
    {
        Kind      = kind;
        Text      = text;
        Bytes     = bytes;
        MediaType = mediaType;
    }

    internal static ToolResult AsText(string text)
        => new(ContentKind.Text, text, System.Array.Empty<byte>(), "");

    internal static ToolResult AsImage(byte[] bytes, string mediaType)
        => new(ContentKind.Image, "", bytes, mediaType);

    public static implicit operator ToolResult(string text) => AsText(text);
}

/// <summary>Adapts a text-returning file-system call to a ToolResult so the delegating tools stay one line.
/// The implicit string conversion can't lift over a Task, so this awaits and converts.</summary>
internal static class ToolResultTasks
{
    internal static async Task<ToolResult> AsToolResult(this Task<string> task) => await task;
}

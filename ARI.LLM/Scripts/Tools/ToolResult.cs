namespace ARI.LLM;

/// <summary>What a tool's Execute produced, and the tag that tells the caller where it goes. Text is
/// injected into the model's context; an image is routed to the vision path (or, when vision is off,
/// rendered back to text by the router). The Kind carries that decision out of the tool so the tool
/// itself never has to know how the model consumes content. A bare string a tool returns becomes a
/// Text result through the implicit conversion, so text tools stay unchanged.</summary>
public readonly struct ToolResult
{
    internal enum ContentKind { Text, Image, Wake }

    internal ContentKind Kind      { get; }
    internal string      Text      { get; }   // set when Kind is Text
    internal byte[]      Bytes     { get; }   // set when Kind is Image
    internal string      MediaType { get; }   // e.g. "image/png"; empty for text
    internal string      Context   { get; }   // set when Kind is Wake — briefing for the new thread
    internal string      Title     { get; }   // set when Kind is Wake — title for the new thread
    internal string      Topic     { get; }   // set when Kind is Wake — the one thing this wake is about
    internal string      VisionNote { get; }  // set when Kind is Image — extra text alongside the image_url part

    private ToolResult(ContentKind kind, string text, byte[] bytes, string mediaType, string context = "", string title = "", string topic = "", string visionNote = "")
    {
        Kind      = kind;
        Text      = text;
        Bytes     = bytes;
        MediaType = mediaType;
        Context   = context;
        Title     = title;
        Topic     = topic;
        VisionNote = visionNote;
    }

    internal static ToolResult AsText(string text)
        => new(ContentKind.Text, text, System.Array.Empty<byte>(), "");

    internal static ToolResult AsImage(byte[] bytes, string mediaType, string visionNote = "")
        => new(ContentKind.Image, "", bytes, mediaType, visionNote: visionNote);

    // content = message sent to the user; context = briefing injected into the new thread's system prompt;
    // topic = the one short thing this wake is about, logged so future dreams don't repeat it.
    internal static ToolResult AsWake(string content, string context, string title = "", string topic = "")
        => new(ContentKind.Wake, content, System.Array.Empty<byte>(), "", context, title, topic);

    public static implicit operator ToolResult(string text) => AsText(text);
}

/// <summary>Adapts a text-returning file-system call to a ToolResult so the delegating tools stay one line.
/// The implicit string conversion can't lift over a Task, so this awaits and converts.</summary>
internal static class ToolResultTasks
{
    internal static async Task<ToolResult> AsToolResult(this Task<string> task) => await task;
}

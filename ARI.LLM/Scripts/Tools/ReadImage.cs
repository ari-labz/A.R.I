namespace ARI.LLM;

/// <summary>An image read is a type of read. It tags raw image bytes — from a local file or a web fetch —
/// so the result router hands them to the vision model (or, when vision is off, renders a text stand-in).
/// The bytes are the image, so there is nothing to fall back to. The source (disk vs web) is chosen by
/// base Read; ReadImage only turns bytes + a media type into an image result.</summary>
internal sealed class ReadImage : Read
{
    internal ReadImage(FileSystem fs) : base(fs) { }

    protected override ToolResult Decode(byte[] raw, string path) => Image(raw, Mime(path));

    /// <summary>Tags bytes as an image. Shared by the local path (mime from extension) and the web path
    /// (mime from the response Content-Type).</summary>
    internal static ToolResult Image(byte[] bytes, string mediaType) => ToolResult.AsImage(bytes, mediaType);

    private static string Mime(string path) => Extension(path) switch
    {
        "png"           => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif"           => "image/gif",
        "webp"          => "image/webp",
        "bmp"           => "image/bmp",
        _               => "application/octet-stream"
    };
}

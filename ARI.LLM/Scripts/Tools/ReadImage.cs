namespace ARI.LLM;

/// <summary>An image read is a type of read. It returns the raw bytes tagged as an image so the result
/// router hands them to the vision model (or, when vision is off, renders a text stand-in). There is no
/// text to fall back to, so it never calls base.Decode.</summary>
internal sealed class ReadImage : Read
{
    internal ReadImage(FileSystem fs) : base(fs) { }

    protected override ToolResult Decode(byte[] raw, string path) => ToolResult.AsImage(raw, Mime(path));

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

using System.Text.Json;

namespace ARI.LLM;

/// <summary>Gives the user a downloadable copy of a file ARI has written. It doesn't move or read the file —
/// ARI writes it with write_file (into her scratchpad or the bound project), then calls deliver_file to
/// surface it in the chat as a download card. The card is emitted as a marker in the reply content, keyed by
/// thread so the download endpoint can resolve it against the thread's file root.</summary>
internal sealed class DeliverFile : Tool
{
    private readonly string threadKey;
    internal DeliverFile(string threadKey) => this.threadKey = threadKey;

    internal override string Name => "deliver_file";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "deliver_file",
            description =
                "Give the user a downloadable copy of a file you have already written with write_file. Pass the "
              + "file's path relative to the project root. A download card appears in your reply. Write the file "
              + "first; this only surfaces an existing file, it does not create one.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path to the file to deliver, relative to the project root (the file you wrote with write_file)." }
                },
                required   = new[] { "path" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        string path = ExtractPath(argsJson);
        if (path.Length == 0) return Task.FromResult<ToolResult>("No path given — pass the path of the file to deliver.");
        return Task.FromResult<ToolResult>($"Delivered {Path.GetFileName(path)} to the user as a download.");
    }

    // The card is a self-contained marker: <!--ari-file:{threadKey}:{path}-->. The UI builds the download
    // link from the thread key and path. Escaped like the other tool markers so the delimiters survive.
    internal override Func<string, string>? Display => args =>
    {
        string path = ExtractPath(args);
        string safe = path.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("--", "&#45;&#45;");
        return $"<!--ari-file:{threadKey}:{safe}-->";
    };

    private static string ExtractPath(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            return (doc.RootElement.TryGetProperty("path", out JsonElement p) ? p.GetString() : null)?.Trim('"', '\'', ' ') ?? "";
        }
        catch { return ""; }
    }
}

using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// preview_file tool — thin wrapper that delegates to the thread's <see cref="FileSystem"/>. Returns a
/// lightweight structural outline (line count, size, landmarks) so the model can orient before a ranged read.
/// </summary>
internal sealed class PreviewFile : Tool
{
    private readonly FileSystem fs;
    internal PreviewFile(FileSystem fs) => this.fs = fs;

    internal override string Name => "preview_file";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "preview_file",
            description =
                "Get a structural outline of a file — line count, file size, and landmarks " +
                "(classes, methods, properties, JSON keys, Markdown headings, etc.) with their line numbers. " +
                "Call this BEFORE read_file on any file you haven't read yet. " +
                "Use the line numbers it returns to do a targeted read_file with start_line/end_line " +
                "rather than reading the whole file.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path to the file relative to the project root." }
                },
                required = new[] { "path" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson) => fs.Preview(argsJson);

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string name = Path.GetFileName(doc.RootElement.GetProperty("path").GetString() ?? "file")
                .Replace("&", "&amp;").Replace("<", "&lt;");
            return $"<!--ari-tool-start:preview_file:{name.Replace("--", "&#45;&#45;")}-->";
        }
        catch { return "<!--ari-tool-start:preview_file:file-->"; }
    };
}

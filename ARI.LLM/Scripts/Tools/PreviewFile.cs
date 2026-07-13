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
                "Get a class-diagram outline of a file — its types with base/interfaces, and every field, " +
                "property and method SIGNATURE with types and line numbers. This answers \"how do I USE this?\" " +
                "and for most files it is ALL you need: to bind to a data class, call a method, or place a " +
                "control, the outline gives you the exact names — no read required. Prefer this over read_file " +
                "by default. Only read_file (a narrow range) when you must see how a specific method BEHAVES " +
                "inside because you are copying it. A preview costs a fraction of a read and keeps your context " +
                "lean, which is what lets you finish.",
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

using System.Text.Json;

namespace ARI.LLM;

/// <summary>move_file tool — thin wrapper that delegates to the thread's <see cref="FileSystem"/>.</summary>
internal sealed class MoveFile : Tool
{
    private readonly FileSystem fs;
    internal MoveFile(FileSystem fs) => this.fs = fs;

    internal override string Name => "move_file";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "move_file",
            description = "Move or rename a file within the project. Creates destination directories as needed. Fails if the destination already exists.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    source      = new { type = "string", description = "Current file path relative to project root." },
                    destination = new { type = "string", description = "New file path relative to project root." }
                },
                required = new[] { "source", "destination" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson) => fs.Move(argsJson);

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string s = doc.RootElement.GetProperty("source").GetString() ?? "";
            return $"<!--ari-tool-start:move_file:{System.IO.Path.GetFileName(s).Replace("&", "&amp;").Replace("<", "&lt;").Replace("--", "&#45;&#45;")}-->";
        }
        catch { return "<!--ari-tool-start:move_file:file-->"; }
    };
}

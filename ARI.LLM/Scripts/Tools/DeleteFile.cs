using System.Text.Json;

namespace ARI.LLM;

/// <summary>delete_file tool — thin wrapper that delegates to the thread's <see cref="FileSystem"/>.</summary>
internal sealed class DeleteFile : Tool
{
    private readonly FileSystem fs;
    internal DeleteFile(FileSystem fs) => this.fs = fs;

    internal override string Name => "delete_file";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "delete_file",
            description = "Delete a file from the project. Use only when explicitly required (e.g. removing a file after merging its contents elsewhere). The user is asked to confirm before the deletion happens.",
            parameters  = new
            {
                type       = "object",
                properties = new { path = new { type = "string", description = "File path relative to project root." } },
                required   = new[] { "path" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson) => fs.Delete(argsJson);

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string p = doc.RootElement.GetProperty("path").GetString() ?? "";
            return $"<!--ari-tool-start:delete_file:{System.IO.Path.GetFileName(p).Replace("&", "&amp;").Replace("<", "&lt;").Replace("--", "&#45;&#45;")}-->";
        }
        catch { return "<!--ari-tool-start:delete_file:file-->"; }
    };
}

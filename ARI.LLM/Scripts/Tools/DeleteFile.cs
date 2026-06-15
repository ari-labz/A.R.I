using System.Text.Json;

namespace ARI.LLM;

/// <summary>Deletes a file within the project root. On the desktop path the renderer gates this
/// behind a user confirmation; the server-side (localPath) path deletes directly.</summary>
internal sealed class DeleteFile : FileTool
{
    internal DeleteFile(string root, CancellationToken ct) : base(root, ct) { }

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

    internal override Task<string> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string relPath = (doc.RootElement.GetProperty("path").GetString() ?? "").Trim('"', '\'', ' ');
            string? abs = Resolve(relPath);
            if (abs is null)         return Task.FromResult("Access denied: path traversal is not allowed.");
            if (!File.Exists(abs))   return Task.FromResult($"File not found: {relPath}");
            File.Delete(abs);
            return Task.FromResult($"Deleted {relPath}.");
        }
        catch (Exception ex) { return Task.FromResult($"Error deleting file: {ex.Message}"); }
    }

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string p = doc.RootElement.GetProperty("path").GetString() ?? "";
            return $"<div class=\"tool-use\">Deleting {System.IO.Path.GetFileName(p).Replace("&", "&amp;").Replace("<", "&lt;")}</div>\n";
        }
        catch { return "<div class=\"tool-use\">Deleting file</div>\n"; }
    };
}

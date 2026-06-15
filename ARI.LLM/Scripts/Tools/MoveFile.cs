using System.Text.Json;

namespace ARI.LLM;

/// <summary>Moves or renames a file within the project root. Refuses to overwrite an existing file.</summary>
internal sealed class MoveFile : FileTool
{
    internal MoveFile(string root, CancellationToken ct) : base(root, ct) { }

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

    internal override Task<string> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string src = (doc.RootElement.GetProperty("source").GetString()      ?? "").Trim('"', '\'', ' ');
            string dst = (doc.RootElement.GetProperty("destination").GetString() ?? "").Trim('"', '\'', ' ');
            string? absSrc = Resolve(src);
            string? absDst = Resolve(dst);
            if (absSrc is null || absDst is null) return Task.FromResult("Access denied: path traversal is not allowed.");
            if (!File.Exists(absSrc))             return Task.FromResult($"Source not found: {src}");
            if (File.Exists(absDst))              return Task.FromResult($"Destination already exists: {dst}. Choose a different name or delete it first.");
            string? dir = Path.GetDirectoryName(absDst);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.Move(absSrc, absDst);
            return Task.FromResult($"Moved {src} → {dst}.");
        }
        catch (Exception ex) { return Task.FromResult($"Error moving file: {ex.Message}"); }
    }

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string s = doc.RootElement.GetProperty("source").GetString() ?? "";
            return $"<div class=\"tool-use\">Moving {System.IO.Path.GetFileName(s).Replace("&", "&amp;").Replace("<", "&lt;")}</div>\n";
        }
        catch { return "<div class=\"tool-use\">Moving file</div>\n"; }
    };
}

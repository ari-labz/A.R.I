using System.Text.Json;

namespace ARI.LLM;

internal sealed class RevertFile : FileTool
{
    private readonly FileSnapshots snapshots;

    internal RevertFile(string root, CancellationToken ct, FileSnapshots snapshots) : base(root, ct)
        => this.snapshots = snapshots;

    internal override string Name => "revert_file";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "revert_file",
            description = "Restore a file to the state it was in immediately before its last edit_file call this session. " +
                          "Use this when an edit produced broken code and you cannot fix it cleanly with a further edit — " +
                          "reverting to the known-good state and re-reading before trying again is always better than " +
                          "incrementally patching a corrupted file. Only the most recent pre-edit snapshot is kept per file; " +
                          "calling revert_file twice on the same file is a no-op (it restores the same snapshot both times). " +
                          "After reverting, always re-read the file to confirm the state before editing again.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path = new { type = "string", description = "File path relative to project root — the same path you passed to edit_file." }
                },
                required = new[] { "path" }
            }
        }
    };

    internal override async Task<string> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string relPath = (doc.RootElement.GetProperty("path").GetString() ?? "").Trim('"', '\'', ' ');
            string? absPath = Resolve(relPath);
            if (absPath is null)
                return "Access denied: path traversal is not allowed.";

            if (!snapshots.TryRestore(absPath, out string content))
                return $"No snapshot for {relPath} — it has not been edited this session, so there is nothing to revert. " +
                       "If you need to start over on a file that was not yet edited, just re-read it and make your edit.";

            await File.WriteAllTextAsync(absPath, content, ct);
            int lines = content.Length == 0 ? 0 : content.Count(c => c == '\n') + 1;
            return $"Reverted {relPath} to its pre-edit state ({lines} lines). " +
                   "Re-read the file to confirm the current content before making any further edits.";
        }
        catch (Exception ex) { return $"Error reverting file: {ex.Message}"; }
    }

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string p = doc.RootElement.GetProperty("path").GetString() ?? "";
            return $"<div class=\"tool-use\">Reverting {p.Replace("&", "&amp;").Replace("<", "&lt;")}</div>\n";
        }
        catch { return "<div class=\"tool-use\">Reverting file</div>\n"; }
    };
}

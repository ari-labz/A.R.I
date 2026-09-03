using System.Text.Json;
using ARI.BrainVault;

namespace ARI.LLM;

// Deletes a brain note through Brain.DeleteNote — never a raw file delete. Rare on purpose: Engram
// never uses this (it only adds/extends), so this exists for Refactor-style cleanup of genuine dead
// stubs/duplicates. Commits immediately so a bad delete is one git revert away from undone.
internal sealed class DeleteMemory : Tool
{
    internal override string Name => "delete_memory";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "delete_memory",
            description = "Permanently delete a note from Ari's brain. Fails with guidance if the note doesn't exist. Commits the deletion to the brain git repo immediately so it's reversible with a git revert.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    name           = new { type = "string", description = "Existing note's name/path, as returned by search_brain or recall_memory." },
                    commit_message = new { type = "string", description = "One-line reason this note is being removed (e.g. 'Duplicate of Alex, merged already handled aliases')." }
                },
                required = new[] { "name", "commit_message" }
            }
        }
    };

    internal override string? PreCheck(Thread thread, string argsJson)
    {
        if (!Brain.Ready) return "Brain is not available right now.";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("name", out JsonElement n) && n.GetString() is { Length: > 0 } name
                && Brain.GetNote(name) is null)
                return $"[Blocked] No note named '{name}' found. Use search_brain to find the correct name.";
        }
        catch { }
        return null;
    }

    internal override Task<ToolResult> Execute(string argsJson)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(argsJson).RootElement; }
        catch { return Task.FromResult<ToolResult>("Error: could not parse arguments."); }

        string name           = root.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
        string commitMessage  = root.TryGetProperty("commit_message", out JsonElement cm) ? cm.GetString() ?? "" : "";

        if (name.Length == 0)          return Task.FromResult<ToolResult>("Error: 'name' is required.");
        if (commitMessage.Length == 0) return Task.FromResult<ToolResult>("Error: 'commit_message' is required.");

        Note? note = Brain.GetNote(name);
        if (note is null) return Task.FromResult<ToolResult>($"No note found for '{name}'.");
        string title = note.Title, path = note.Path;

        try { Brain.DeleteNote(name); }
        catch (Exception ex) { return Task.FromResult<ToolResult>($"Failed to delete note: {ex.Message}"); }

        string commitResult = BrainGit.Commit(Brain.VaultRoot, commitMessage);
        return Task.FromResult<ToolResult>($"Deleted '{title}' ({path}).\n{commitResult}");
    }
}

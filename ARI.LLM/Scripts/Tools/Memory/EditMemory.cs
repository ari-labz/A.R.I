using System.Text.Json;
using ARI.BrainVault;

namespace ARI.LLM;

// Edits an existing brain note through Brain.EditNote — never a raw file write. EditNote knows a
// note's real shape (sticky frontmatter fields, rename-with-merge, reference repointing), so this is
// safe on notes a plain overwrite would otherwise corrupt. Use create_memory if the note is new.
internal sealed class EditMemory : Tool
{
    internal override string Name => "edit_memory";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "edit_memory",
            description = "Replace the content of an existing brain note (whole-note replacement, not a line edit). Optionally rename it, retype it, or change its alias/keyword/sensitivity fields — omitted fields are left as they are. Fails with guidance if the note doesn't exist. Commits to the brain git repo immediately.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    name           = new { type = "string", description = "Existing note's current name/path, as returned by search_brain or recall_memory." },
                    content        = new { type = "string", description = "The note's full replacement body (markdown, dense linked prose). This replaces the whole note content." },
                    aliases        = new { type = "array", items = new { type = "string" }, description = "Full replacement alias list. Omit to keep the note's existing aliases." },
                    new_name       = new { type = "string", description = "Rename the note to this name/path. Old title becomes an alias; references are repointed automatically." },
                    type           = new { type = "string", description = "Node type, e.g. 'hub', 'person', 'project'. Omit to leave unchanged." },
                    keywords       = new { type = "array", items = new { type = "string" }, description = "Full replacement keyword list. Omit to leave unchanged." },
                    is_sensitive   = new { type = "boolean", description = "True if this note holds private/intimate content. Omit to leave unchanged." },
                    commit_message = new { type = "string", description = "One-line summary of what changed and why (e.g. 'Add job change — confirmed new role starts next month')." }
                },
                required = new[] { "name", "content", "commit_message" }
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
                return $"[Blocked] No note named '{name}' found. Use search_brain to find the correct name, or create_memory if it doesn't exist yet.";
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
        string content        = root.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
        string commitMessage  = root.TryGetProperty("commit_message", out JsonElement cm) ? cm.GetString() ?? "" : "";
        string? newName       = root.TryGetProperty("new_name", out JsonElement nn) && nn.ValueKind == JsonValueKind.String ? nn.GetString() : null;
        string? type          = root.TryGetProperty("type", out JsonElement ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() : null;
        bool? isSensitive     = root.TryGetProperty("is_sensitive", out JsonElement isv) && isv.ValueKind is JsonValueKind.True or JsonValueKind.False ? isv.GetBoolean() : null;
        bool aliasesGiven     = root.TryGetProperty("aliases", out JsonElement al) && al.ValueKind == JsonValueKind.Array;
        bool keywordsGiven    = root.TryGetProperty("keywords", out JsonElement kw) && kw.ValueKind == JsonValueKind.Array;
        List<string>? aliases = aliasesGiven ? al.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList() : null;
        List<string>? keywords = keywordsGiven ? kw.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList() : null;

        if (name.Length == 0)          return Task.FromResult<ToolResult>("Error: 'name' is required.");
        if (content.Length == 0)       return Task.FromResult<ToolResult>("Error: 'content' is required.");
        if (commitMessage.Length == 0) return Task.FromResult<ToolResult>("Error: 'commit_message' is required.");

        Note existing = Brain.GetNote(name)!;
        List<string> finalAliases = aliases ?? existing.Aliases.ToList();

        Note note;
        try { note = Brain.EditNote(name, content, finalAliases, newName, type, keywords, isSensitive); }
        catch (Exception ex) { return Task.FromResult<ToolResult>($"Failed to edit note: {ex.Message}"); }

        string commitResult = BrainGit.Commit(Brain.VaultRoot, commitMessage);
        return Task.FromResult<ToolResult>($"Updated '{note.Title}' at {note.Path}.\n{commitResult}");
    }
}

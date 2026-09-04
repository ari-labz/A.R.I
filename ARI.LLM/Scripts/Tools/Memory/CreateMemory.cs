using System.Text.Json;
using ARI.BrainVault;

namespace ARI.LLM;

// Creates a new brain note through Brain.AddNote — never a raw file write. Use edit_memory if it already exists.
internal sealed class CreateMemory : Tool
{
    internal override string Name => "create_memory";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "create_memory",
            description = "Create a new note in Ari's brain. Fails with guidance if the note already exists — use edit_memory to change an existing one. Commits to the brain git repo immediately.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    name           = new { type = "string", description = "Note name/path, e.g. 'People/Alex'. Information about a user's private life is not a separate path — it lives on the ordinary note, flagged with is_sensitive below." },
                    content        = new { type = "string", description = "The note BODY ONLY — dense linked prose, starting directly with the text. Do NOT include a YAML frontmatter block (a '---' fenced section) and do NOT include a '# Title' heading — the title comes from `name` and frontmatter is generated automatically from the type/keywords/is_sensitive/aliases parameters below. Writing them again here corrupts the file." },
                    aliases        = new { type = "array", items = new { type = "string" }, description = "Alternate names this note should also be found under." },
                    type           = new { type = "string", description = "Node type, e.g. 'hub', 'person', 'project'. Omit for a plain leaf note." },
                    keywords       = new { type = "array", items = new { type = "string" }, description = "Search keywords beyond the title/aliases." },
                    is_sensitive   = new { type = "boolean", description = "True if this note holds information about a user's private life." },
                    commit_message = new { type = "string", description = "One-line summary of what this note captures and why it's being added now." }
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
                && Brain.GetNote(name) is not null)
                return $"[Blocked] A note named '{name}' already exists. Use edit_memory to change it instead of create_memory.";
        }
        catch { }
        return null;
    }

    internal override Task<ToolResult> Execute(string argsJson)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(argsJson).RootElement; }
        catch { return Task.FromResult<ToolResult>("Error: could not parse arguments."); }

        string name          = root.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
        string content       = root.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
        string commitMessage = root.TryGetProperty("commit_message", out JsonElement cm) ? cm.GetString() ?? "" : "";
        string? type         = root.TryGetProperty("type", out JsonElement ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() : null;
        bool? isSensitive    = root.TryGetProperty("is_sensitive", out JsonElement isv) && isv.ValueKind is JsonValueKind.True or JsonValueKind.False ? isv.GetBoolean() : null;
        List<string> aliases = root.TryGetProperty("aliases", out JsonElement al) && al.ValueKind == JsonValueKind.Array
            ? al.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList() : new();
        List<string> keywords = root.TryGetProperty("keywords", out JsonElement kw) && kw.ValueKind == JsonValueKind.Array
            ? kw.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList() : new();

        if (name.Length == 0)          return Task.FromResult<ToolResult>("Error: 'name' is required.");
        if (content.Length == 0)       return Task.FromResult<ToolResult>("Error: 'content' is required.");
        if (commitMessage.Length == 0) return Task.FromResult<ToolResult>("Error: 'commit_message' is required.");

        content = Brain.StripAccidentalFrontmatter(content, name);
        if (content.Length == 0) return Task.FromResult<ToolResult>("Error: 'content' is required.");

        Note note;
        try { note = Brain.AddNote(name, content, aliases, type, keywords.Count > 0 ? keywords : null, isSensitive); }
        catch (Exception ex) { return Task.FromResult<ToolResult>($"Failed to create note: {ex.Message}"); }

        string commitResult = BrainGit.Commit(Brain.VaultRoot, commitMessage, note.Path);
        return Task.FromResult<ToolResult>($"Created '{note.Title}' at {note.Path}.\n{commitResult}");
    }
}

using System.Text.Json;
using ARI.BrainVault;

namespace ARI.LLM;

// Edits an existing brain note through Brain.PatchNote — a targeted old_string/new_string replace
// (same contract as Claude Code's own Edit tool), never a whole-note retype. Use create_memory if the
// note is new.
internal sealed class EditMemory : Tool
{
    // Scope guard for a batched sweep — confines edits to the notes it already resolved. Null (the
    // ToolFactories default) means unrestricted.
    private readonly IReadOnlyCollection<string>? allowedNames;
    private readonly IReadOnlyCollection<string>? allowedPrefixes;
    internal EditMemory(IReadOnlyCollection<string>? allowedNames = null, IReadOnlyCollection<string>? allowedPrefixes = null)
    {
        this.allowedNames = allowedNames;
        this.allowedPrefixes = allowedPrefixes;
    }

    internal override string Name => "edit_memory";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "edit_memory",
            description = "Replace one exact occurrence of old_string with new_string inside an existing brain note's body — a targeted edit, not a whole-note rewrite. old_string must match the note's current text exactly (including whitespace) and must be unique in the note; include enough surrounding context to make it unambiguous. To delete text, pass an empty new_string. To insert text, make old_string the line it should go before/after and include that line in new_string too. Optionally rename the note, retype it, or change its alias/keyword/sensitivity fields in the same call — omitted fields are left as they are. Fails with guidance if old_string isn't found, matches more than once, or the note doesn't exist. Commits to the brain git repo immediately.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    name           = new { type = "string", description = "Existing note's current name/path, as returned by search_brain or recall_memory." },
                    old_string     = new { type = "string", description = "Exact text to find in the note's current body — must match exactly once. Read the note first (recall_memory) if you're not certain of its exact wording." },
                    new_string     = new { type = "string", description = "Text to replace old_string with. Empty string deletes old_string outright." },
                    aliases        = new { type = "array", items = new { type = "string" }, description = "Full replacement alias list. Omit to keep the note's existing aliases." },
                    new_name       = new { type = "string", description = "Rename the note to this name/path. Old title becomes an alias; references are repointed automatically." },
                    type           = new { type = "string", description = "Node type, e.g. 'hub', 'person', 'project'. Omit to leave unchanged." },
                    keywords       = new { type = "array", items = new { type = "string" }, description = "Full replacement keyword list. Omit to leave unchanged." },
                    is_sensitive   = new { type = "boolean", description = "True if the note is entirely about a user's private life. For a note that's mostly ordinary with just one sensitive line or section, leave this unchanged and mark that span with a `> [!sensitive]` callout in new_string instead. Omit to leave unchanged." },
                    commit_message = new { type = "string", description = "One-line summary of what changed and why (e.g. 'Add job change — confirmed new role starts next month')." }
                },
                required = new[] { "name", "old_string", "new_string", "commit_message" }
            }
        }
    };

    internal override string? PreCheck(Thread thread, string argsJson)
    {
        if (!Brain.Ready) return "Brain is not available right now.";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("name", out JsonElement n) && n.GetString() is { Length: > 0 } name)
            {
                if (Brain.GetNote(name) is null)
                    return $"[Blocked] No note named '{name}' found. Use search_brain to find the correct name, or create_memory if it doesn't exist yet.";

                if (allowedNames is not null || allowedPrefixes is not null)
                {
                    bool ok = (allowedNames?.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) ?? false)
                           || (allowedPrefixes?.Any(pre => name.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) ?? false);
                    if (!ok)
                    {
                        List<string> allowed = new(allowedNames ?? Array.Empty<string>());
                        if (allowedPrefixes is not null) allowed.AddRange(allowedPrefixes.Select(pre => $"{pre}*"));
                        return $"[Blocked] This call may only edit one of: {string.Join(", ", allowed)}.";
                    }
                }
            }
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
        string oldString      = root.TryGetProperty("old_string", out JsonElement os) ? os.GetString() ?? "" : "";
        string newString      = root.TryGetProperty("new_string", out JsonElement ns) ? ns.GetString() ?? "" : "";
        string commitMessage  = root.TryGetProperty("commit_message", out JsonElement cm) ? cm.GetString() ?? "" : "";
        string? newName       = root.TryGetProperty("new_name", out JsonElement nn) && nn.ValueKind == JsonValueKind.String ? nn.GetString() : null;
        string? type          = root.TryGetProperty("type", out JsonElement ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() : null;
        bool? isSensitive     = root.TryGetProperty("is_sensitive", out JsonElement isv) && isv.ValueKind is JsonValueKind.True or JsonValueKind.False ? isv.GetBoolean() : null;
        bool aliasesGiven     = root.TryGetProperty("aliases", out JsonElement al) && al.ValueKind == JsonValueKind.Array;
        bool keywordsGiven    = root.TryGetProperty("keywords", out JsonElement kw) && kw.ValueKind == JsonValueKind.Array;
        List<string>? aliases = aliasesGiven ? al.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList() : null;
        List<string>? keywords = keywordsGiven ? kw.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList() : null;

        if (name.Length == 0)          return Task.FromResult<ToolResult>("Error: 'name' is required.");
        if (oldString.Length == 0)     return Task.FromResult<ToolResult>("Error: 'old_string' is required.");
        if (commitMessage.Length == 0) return Task.FromResult<ToolResult>("Error: 'commit_message' is required.");

        Note existing = Brain.GetNote(name)!;
        string oldPath = existing.Path;

        Note note;
        List<string> repointed = new();
        try { note = Brain.PatchNote(name, oldString, newString, aliases, newName, type, keywords, isSensitive, repointed); }
        catch (Exception ex) { return Task.FromResult<ToolResult>($"Failed to edit note: {ex.Message}"); }

        // A rename also repoints [[oldTitle]] in every referrer — stage those too, not just the note.
        List<string> touched = new() { note.Path };
        if (note.Path != oldPath) touched.Add(oldPath);
        touched.AddRange(repointed);
        string commitResult = BrainGit.Commit(Brain.VaultRoot, commitMessage, touched.ToArray());
        return Task.FromResult<ToolResult>($"Updated '{note.Title}' at {note.Path}.\n{commitResult}");
    }
}

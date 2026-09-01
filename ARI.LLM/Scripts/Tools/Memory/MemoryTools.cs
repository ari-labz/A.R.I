using System.Text.Json;
using ARI.Brain;

namespace ARI.LLM;

// Brain-specific tools for the memory agents. Note content is read/written through the ordinary file
// tools (read_file / write_file / edit_file / move_file / delete_file) pointed at the vault — the vault
// IS a markdown filesystem. These tools cover only what a plain file op cannot express: the graph
// skeleton, structural merge, git history, and the curiosity queue.

file static class Args
{
    internal static JsonElement Parse(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement; }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }
    internal static string Str(this JsonElement el, string prop, string fallback = "")
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    internal static int Int(this JsonElement el, string prop, int fallback)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
    internal static List<string> Arr(this JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : new();
}

// ── neighbours ─────────────────────────────────────────────────────────────────────────

// The adjacency skeleton BFS-reachable from a seed. Reindexes first so it always reflects edits the
// agent just made through the file tools.
internal sealed class Neighbours : Tool
{
    internal override string     Name   => "neighbours";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "neighbours",
            description = "Return the neighbourhood around a seed note as an adjacency skeleton (path [type], ← inbound, → outbound).",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    seed  = new { type = "string", description = "Title or path of the note to start from." },
                    depth = new { type = "integer", description = "Max hops out from the seed (default 6)." },
                    cap   = new { type = "integer", description = "Max nodes to return, nearest first (default 1000)." }
                },
                required = new[] { "seed" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        JsonElement a = Args.Parse(argsJson);
        string seed = a.Str("seed");
        if (seed.Length == 0) return Task.FromResult<ToolResult>("Error: 'seed' is required.");
        BrainModule.Index();
        string? skeleton = BrainModule.Skeleton(seed, a.Int("depth", 2), a.Int("cap", 50));
        return Task.FromResult<ToolResult>(skeleton is null ? $"No note found for seed '{seed}'." :
            skeleton.Length == 0 ? $"'{seed}' has no connections." : skeleton);
    }
}

// ── search_brain ─────────────────────────────────────────────────────────────────────

// Alias-aware lookup over the memory index. Where a raw file search sees only filenames (find_files) or
// literal text (search_files), this resolves by note title, alias, AND content — so an entity referred to
// by an alias (e.g. "Al" → the "Alex" note) is still found. Reindexes first so results reflect edits
// the agent just made. Ranked: exact title, then alias, then content.
internal sealed class SearchBrain : Tool
{
    private const int DEFAULT_LIMIT = 15;

    internal override string     Name   => "search_brain";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "search_brain",
            description = "Search notes by title, alias, or content. Plain words only (not regex). Returns 'title — path' ranked by relevance.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    query = new { type = "string",  description = "Plain words to look up, e.g. 'Alex' or 'holiday plans'. Not a regex or glob." },
                    limit = new { type = "integer", description = $"Max results to return (default {DEFAULT_LIMIT})." }
                },
                required = new[] { "query" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        JsonElement a = Args.Parse(argsJson);
        string query = a.Str("query").Trim();
        if (query.Length == 0) return Task.FromResult<ToolResult>("Error: 'query' is required.");
        int limit = Math.Clamp(a.Int("limit", DEFAULT_LIMIT), 1, 50);

        BrainModule.Index();
        List<string> terms = System.Text.RegularExpressions.Regex
            .Split(query, @"[^a-zA-Z0-9']+")
            .Select(t => t.Trim('\''))
            .Where(t => t.Length > 0)
            .ToList();
        List<SearchResult> hits = BrainModule.Search(terms, limit);
        if (hits.Count == 0) return Task.FromResult<ToolResult>($"No notes found for '{query}'. It likely has no note yet.");

        return Task.FromResult<ToolResult>(string.Join('\n', hits.Select(h => $"{h.Note.Title} — {h.Note.Path}")));
    }
}

// ── merge_notes ────────────────────────────────────────────────────────────────────────

// Fold one note into another: the loser's title + aliases become aliases on the winner, every
// [[loser]] reference is repointed, and the loser file is deleted. This is a structural graph op that
// file edits can't express cleanly (the reference repoint is graph-wide).
internal sealed class MergeNotesTool : Tool
{
    internal override string Name => "merge_notes";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "merge_notes",
            description = "Merge duplicate notes. 'from' is folded into 'into': aliases transferred, references repointed, 'from' deleted.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    from = new { type = "string", description = "Title of the duplicate to fold away (the loser)." },
                    into = new { type = "string", description = "Title of the canonical note to keep (the winner)." }
                },
                required = new[] { "from", "into" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        JsonElement a = Args.Parse(argsJson);
        string from = a.Str("from"), into = a.Str("into");
        if (from.Length == 0 || into.Length == 0) return Task.FromResult<ToolResult>("Error: both 'from' and 'into' are required.");
        try
        {
            bool ok = BrainModule.MergeNotes(from, into);
            return Task.FromResult<ToolResult>(ok ? $"Merged '{from}' into '{into}' ('{from}' kept as an alias)."
                                      : $"Merge failed — '{from}' or '{into}' not found, or they are the same note.");
        }
        catch (Exception ex) { return Task.FromResult<ToolResult>($"Merge failed: {ex.Message}"); }
    }
}

// ── recall_memory ──────────────────────────────────────────────────────────────────────

// Reads the full content of a brain note by path or title.
// Use search_brain first to find the path, then recall_memory to read it.
// In non-owner conversations: only recall notes that are non-sensitive and appropriate to share.
internal sealed class RecallMemory : Tool
{
    internal override string     Name   => "recall_memory";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "recall_memory",
            description = "Read the full content of a brain note by its path or title. Use search_brain first to find the path, then call this to read it. The note path comes from search_brain results (e.g. 'People/Xywren.md'). In conversations with someone other than the owner, only recall notes whose content is non-sensitive and appropriate to share with a third party.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path = new { type = "string", description = "Note path or title, as returned by search_brain." }
                },
                required = new[] { "path" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        string path = Args.Parse(argsJson).Str("path").Trim();
        if (path.Length == 0) return Task.FromResult<ToolResult>("Error: 'path' is required.");

        BrainModule.Index();
        Note? note = BrainModule.GetNote(path);
        if (note is null) return Task.FromResult<ToolResult>($"No note found for '{path}'. Use search_brain to find the correct path.");

        string content = note.Content;
        if (content.Trim().Length == 0) return Task.FromResult<ToolResult>($"'{note.Title}' exists but has no content yet.");
        return Task.FromResult<ToolResult>($"# {note.Title}\n\n{content}");
    }
}

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
    internal override string Name => "neighbours";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "neighbours",
            description = "Walk the memory graph from a seed note and return its neighbourhood as an adjacency skeleton: one block per node showing its full path, [type], inbound connections ('<') and outbound connections ('>'). Use this to see how a region is wired before deciding what to change.",
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

    internal override Task<string> Execute(string argsJson)
    {
        JsonElement a = Args.Parse(argsJson);
        string seed = a.Str("seed");
        if (seed.Length == 0) return Task.FromResult("Error: 'seed' is required.");
        BrainModule.Index();
        string? skeleton = BrainModule.Skeleton(seed, a.Int("depth", 6), a.Int("cap", 1000));
        return Task.FromResult(skeleton is null ? $"No note found for seed '{seed}'." :
            skeleton.Length == 0 ? $"'{seed}' has no connections." : skeleton);
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
            description = "Merge two notes that are the same entity. The 'from' note is folded into 'into': its title and aliases become searchable aliases on 'into', all references are repointed, and 'from' is deleted. Use when you learn (or are told) that two notes are the same person/place/thing.",
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

    internal override Task<string> Execute(string argsJson)
    {
        JsonElement a = Args.Parse(argsJson);
        string from = a.Str("from"), into = a.Str("into");
        if (from.Length == 0 || into.Length == 0) return Task.FromResult("Error: both 'from' and 'into' are required.");
        try
        {
            bool ok = BrainModule.MergeNotes(from, into);
            return Task.FromResult(ok ? $"Merged '{from}' into '{into}' ('{from}' kept as an alias)."
                                      : $"Merge failed — '{from}' or '{into}' not found, or they are the same note.");
        }
        catch (Exception ex) { return Task.FromResult($"Merge failed: {ex.Message}"); }
    }
}

// ── curiosities ────────────────────────────────────────────────────────────────────────

// Things Ari wants to ask Xywren about. The agent can add and remove. Ari's own note-anchored thoughts
// stay as inline callouts (a separate sink) — these are the ask-later queue consumed by proactive messaging.
internal sealed class AddCuriosity : Tool
{
    private readonly string persistentDir;
    internal AddCuriosity(string persistentDir) => this.persistentDir = persistentDir;

    internal override string Name => "add_curiosity";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "add_curiosity",
            description = "Record something Ari is curious about and would like to ask Xywren later. Use for genuine open questions surfaced while tidying the graph — not for facts (those go in notes).",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    question = new { type = "string", description = "What Ari would actually ask." },
                    topic    = new { type = "string", description = "The main entity/subject it's about." },
                    keywords = new { type = "array", items = new { type = "string" }, description = "Words to match against future conversation topics." },
                    reason   = new { type = "string", description = "Why she's curious (for her own record)." },
                    priority = new { type = "integer", description = "1 (idle wondering) .. 5 (really wants to know)." }
                },
                required = new[] { "question", "topic" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        JsonElement a = Args.Parse(argsJson);
        string question = a.Str("question"), topic = a.Str("topic");
        if (question.Length == 0 || topic.Length == 0) return Task.FromResult("Error: 'question' and 'topic' are required.");
        Curiosity c = new(
            Id: Guid.NewGuid().ToString("N")[..8],
            Question: question,
            Topic: topic,
            Keywords: a.Arr("keywords"),
            Reason: a.Str("reason", string.Empty),
            Priority: Math.Clamp(a.Int("priority", 2), 1, 5),
            Status: "pending",
            Created: DateTime.UtcNow.ToString("yyyy-MM-dd"),
            AskedAt: null);
        int added = CuriosityStore.AddNew(persistentDir, new[] { c });
        return Task.FromResult(added > 0 ? $"Curiosity recorded ({c.Id}): {question}" : "Already have that curiosity queued.");
    }
}

internal sealed class RemoveCuriosity : Tool
{
    private readonly string persistentDir;
    internal RemoveCuriosity(string persistentDir) => this.persistentDir = persistentDir;

    internal override string Name => "remove_curiosity";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "remove_curiosity",
            description = "Remove a curiosity from the ask-later queue by its id (e.g. one that's no longer interesting or has been resolved). Use list_curiosities to see ids.",
            parameters  = new
            {
                type       = "object",
                properties = new { id = new { type = "string", description = "The curiosity id to remove." } },
                required   = new[] { "id" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        string id = Args.Parse(argsJson).Str("id");
        if (id.Length == 0) return Task.FromResult("Error: 'id' is required.");
        return Task.FromResult(CuriosityStore.Remove(persistentDir, id) ? $"Removed curiosity {id}." : $"No curiosity with id {id}.");
    }
}

internal sealed class ListCuriosities : Tool
{
    private readonly string persistentDir;
    internal ListCuriosities(string persistentDir) => this.persistentDir = persistentDir;

    internal override string Name => "list_curiosities";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "list_curiosities",
            description = "List the curiosities currently queued (id, priority, topic, question) so you can decide whether to add a new one or remove a stale one.",
            parameters  = new { type = "object", properties = new { } }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        List<Curiosity> list = CuriosityStore.Load(persistentDir);
        if (list.Count == 0) return Task.FromResult("No curiosities queued.");
        IEnumerable<string> lines = list
            .OrderByDescending(c => c.Priority)
            .Select(c => $"[{c.Id}] (p{c.Priority}, {c.Status}) {c.Topic}: {c.Question}");
        return Task.FromResult(string.Join('\n', lines));
    }
}

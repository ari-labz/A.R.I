using System.Text;
using System.Text.Json;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Recall : Agent
{
    private readonly BrainService brain;
    private readonly int recallDepth;
    private readonly string brainPublicUrl;

    internal Recall(ModelConfig config, BrainService brain, int recallDepth, string brainPublicUrl) : base(config)
    {
        this.brain          = brain;
        this.recallDepth    = recallDepth;
        this.brainPublicUrl = brainPublicUrl;
    }

    /// <summary>
    /// Searches Brain for notes relevant to the current conversation history.
    /// Runs up to <see cref="recallDepth"/> recursive fetch steps, parallelising note retrieval
    /// within each step for speed. Returns null if nothing relevant is found.
    /// </summary>
    internal async Task<string?> FetchContextAsync(IReadOnlyList<ThreadItem> history, string incomingPrompt)
    {
        if (recallDepth <= 0) return null;

        List<string> allTitles = await brain.GetNoteTitles();
        if (allTitles.Count == 0) return null;

        // Fast pre-flight: skip the LLM entirely for short messages that mention no known
        // entity and contain no memory-seeking language. No inference cost at all.
        if (ShouldSkip(incomingPrompt, allTitles))
        {
            Common.Logger.LogInformation("[Recall] skipped — short message with no known entities or memory keywords.");
            return null;
        }

        // Include the incoming prompt so Recall can search based on what was just asked,
        // not only prior history (which may be empty on the first message of a thread).
        string transcript = BuildTranscript(history, incomingPrompt);

        // Use full paths (e.g. "People/[REDACT]'s Family/Immediate Family/[REDACT]") so the model
        // can infer relationships and categories from the path before fetching anything.
        // Note fetches still use bare titles — the path is for identification only.
        List<string> allPaths    = await brain.GetNotePaths();
        string allPathsList      = string.Join(", ", allPaths);
        string recallThreadKey   = $"recall:{Guid.NewGuid()}";
        HashSet<string> fetched  = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (string Content, string? NoteId)> noteContents = new(StringComparer.OrdinalIgnoreCase);

        // First fetch prompt — stateless, no prior notes.
        // Paths reveal the graph structure so the model can reason about relationships
        // (e.g. "People/[REDACT]'s Family/Immediate Family/[REDACT]" = [REDACT]'s mum) without fetching first.
        // Respond with bare note TITLES (not full paths) — that's what the fetch system uses.
        string firstPrompt =
            "You are recalling memories for a conversation. " +
            "The available notes are shown as full paths — the path encodes meaning (category, ownership, relationship). " +
            "Use the paths to identify which notes are relevant without needing to fetch first.\n\n" +
            $"CONVERSATION:\n{transcript}\n\n" +
            $"AVAILABLE NOTES (full paths): {allPathsList}\n\n" +
            "Respond ONLY with JSON using bare note TITLES (the last segment of the path): " +
            "{\"fetch\": [\"[REDACT]\", \"[REDACT]'s Family\"]} — or {\"fetch\": []} if nothing is relevant.";

        string raw = await PromptThread(recallThreadKey, firstPrompt);

        for (int depth = 0; depth < recallDepth; depth++)
        {
            List<string> toFetch = ParseFetchList(raw)
                .Where(n => !fetched.Contains(n))
                .ToList();

            if (toFetch.Count == 0) break;

            Common.Logger.LogInformation("[Recall] depth {Depth}: fetching [{Notes}]",
                depth + 1, string.Join(", ", toFetch));

            // Fetch all requested notes in parallel. Brain handles caching internally —
            // cache hits return instantly; misses hit Trilium and are cached for next time.
            IEnumerable<Task<(string name, string? content, string? noteId)>> fetchTasks = toFetch.Select(async name =>
            {
                string? content = await brain.GetNote(name);
                string? noteId  = content is not null ? await brain.GetNoteId(name) : null;
                return (name, content, noteId);
            });

            IEnumerable<(string name, string? content, string? noteId)> results = await Task.WhenAll(fetchTasks);

            StringBuilder notesBlock = new();
            foreach ((string name, string? content, string? noteId) in results)
            {
                if (content is null) continue;
                fetched.Add(name);
                noteContents[name] = (content, noteId);
                notesBlock.AppendLine($"--- {name} ---");
                notesBlock.AppendLine(content);
                notesBlock.AppendLine("---");
            }

            if (depth + 1 >= recallDepth) break;

            // Bundle the fetched notes and the next fetch request into a single prompt,
            // avoiding a wasted inference round-trip on note delivery alone.
            string nextPrompt =
                $"Here are the notes you requested:\n\n{notesBlock}\n" +
                $"Based on any [[links]] or references in those notes, are there further notes you want? " +
                $"Do NOT re-request notes already fetched: {string.Join(", ", fetched)}.\n" +
                "Respond ONLY with JSON: {\"fetch\": [\"Name\"]} — or {\"fetch\": []} to stop.";

            raw = await PromptThread(recallThreadKey, nextPrompt);
        }

        if (noteContents.Count == 0) return null;

        StringBuilder result = new();
        foreach (KeyValuePair<string, (string Content, string? NoteId)> kvp in noteContents)
        {
            string header = kvp.Value.NoteId is not null
                ? $"[{kvp.Key}|{brainPublicUrl}/#root/{kvp.Value.NoteId}]"
                : $"[{kvp.Key}]";
            result.AppendLine(header);
            result.AppendLine(kvp.Value.Content);
            result.AppendLine();
        }

        Common.Logger.LogInformation("[Recall] recalled {Count} note(s): {Names}",
            noteContents.Count, string.Join(", ", noteContents.Keys));

        return result.ToString().TrimEnd();
    }

    // ── Private ──────────────────────────────────────────────────────────────────

    // Explicit memory-seeking phrases — reaching back in time or referencing prior context.
    private static readonly string[] MemoryKeywords =
    [
        "remember", "last time", "before", "yesterday", "you said", "we talked",
        "earlier", "previously", "used to", "told me", "you mentioned", "we discussed"
    ];

    // Personal-context indicators — possessive or relational language signals the user is
    // asking about their own life, which is exactly what the brain stores.
    private static readonly string[] PersonalKeywords =
    [
        " my ", "my ", "who is", "who's", "who are", "what is", "what's",
        "where is", "where's", "tell me about", "what do you know"
    ];

    /// <summary>
    /// Returns true if Recall can safely be skipped without any LLM call.
    /// Skips only when ALL four conditions hold:
    ///   1. The message is short (under 80 chars — likely a greeting or brief social exchange).
    ///   2. No known note title appears in the message.
    ///   3. No memory-seeking keyword is present.
    ///   4. No personal-context indicator is present (possessive/relational language or a question).
    /// If any condition fails we fall through to the normal LLM path.
    /// </summary>
    private static bool ShouldSkip(string message, List<string> noteTitles)
    {
        if (message.Length >= 80) return false;

        // A question mark means the user is asking something — likely needs memory.
        if (message.Contains('?')) return false;

        foreach (string kw in MemoryKeywords)
            if (message.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return false;

        foreach (string kw in PersonalKeywords)
            if (message.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return false;

        foreach (string title in noteTitles)
            if (message.Contains(title, StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }

    private static string BuildTranscript(IReadOnlyList<ThreadItem> history, string incomingPrompt)
    {
        StringBuilder sb = new();
        foreach (ThreadItem item in history)
        {
            switch (item)
            {
                case UserMessage u: sb.AppendLine($"{u.Username}: {u.Content}"); break;
                case AriResponse r: sb.AppendLine($"ARI: {r.Content}");          break;
            }
        }
        sb.AppendLine($"User: {incomingPrompt}");
        return sb.ToString();
    }

    private static List<string> ParseFetchList(string raw)
    {
        try
        {
            raw = raw.Trim();
            int start = raw.IndexOf('{');
            if (start >= 0) raw = raw[start..];
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("fetch", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
        }
        catch { }
        return new List<string>();
    }
}

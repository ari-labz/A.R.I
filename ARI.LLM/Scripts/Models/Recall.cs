using System.Text;
using System.Text.Json;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Recall : Model
{
    private readonly BrainService brain;
    private readonly int recallDepth;

    internal Recall(ModelConfig config, BrainService brain, int recallDepth) : base(config)
    {
        this.brain       = brain;
        this.recallDepth = recallDepth;
    }

    /// <summary>
    /// Searches Brain for notes relevant to the current conversation history.
    /// Runs up to <see cref="recallDepth"/> recursive fetch steps, parallelising note retrieval
    /// within each step for speed. Returns null if nothing relevant is found.
    /// </summary>
    internal async Task<string?> FetchContextAsync(IReadOnlyList<ChatMessage> history, string incomingPrompt)
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
        string allTitlesList     = string.Join(", ", allTitles);
        string recallThreadKey   = $"recall:{Guid.NewGuid()}";
        HashSet<string> fetched  = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> noteContents = new(StringComparer.OrdinalIgnoreCase);

        // First fetch prompt — stateless, no prior notes.
        string firstPrompt =
            "You are recalling memories for a conversation. " +
            "Given the conversation and the list of available notes, identify which notes are relevant.\n\n" +
            $"CONVERSATION:\n{transcript}\n\n" +
            $"AVAILABLE NOTES: {allTitlesList}\n\n" +
            "Respond ONLY with JSON: {\"fetch\": [\"Name1\", \"Name2\"]} — or {\"fetch\": []} if nothing is relevant.";

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
            IEnumerable<Task<(string name, string? content)>> fetchTasks = toFetch.Select(async name =>
            {
                string? content = await brain.GetNote(name);
                return (name, content);
            });

            IEnumerable<(string name, string? content)> results = await Task.WhenAll(fetchTasks);

            StringBuilder notesBlock = new();
            foreach ((string name, string? content) in results)
            {
                if (content is null) continue;
                fetched.Add(name);
                noteContents[name] = content;
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
        foreach (KeyValuePair<string, string> kvp in noteContents)
        {
            result.AppendLine($"[{kvp.Key}]");
            result.AppendLine(kvp.Value);
            result.AppendLine();
        }

        Common.Logger.LogInformation("[Recall] recalled {Count} note(s): {Names}",
            noteContents.Count, string.Join(", ", noteContents.Keys));

        return result.ToString().TrimEnd();
    }

    // ── Private ──────────────────────────────────────────────────────────────────

    // Words/phrases that signal the user is reaching into memory even without naming an entity.
    private static readonly string[] MemoryKeywords =
    [
        "remember", "last time", "before", "yesterday", "you said", "we talked",
        "earlier", "previously", "used to", "told me", "you mentioned", "we discussed"
    ];

    /// <summary>
    /// Returns true if Recall can safely be skipped without any LLM call.
    /// Skips only when ALL three conditions hold:
    ///   1. The message is short (under 50 chars — likely a greeting or brief social exchange).
    ///   2. No known note title appears in the message (no named entity to look up).
    ///   3. No memory-seeking keyword is present (user isn't explicitly reaching back in time).
    /// If any condition fails we fall through to the normal LLM path.
    /// </summary>
    private static bool ShouldSkip(string message, List<string> noteTitles)
    {
        if (message.Length >= 50) return false;

        foreach (string kw in MemoryKeywords)
            if (message.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return false;

        foreach (string title in noteTitles)
            if (message.Contains(title, StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }

    private static string BuildTranscript(IReadOnlyList<ChatMessage> history, string incomingPrompt)
    {
        StringBuilder sb = new();
        foreach (ChatMessage msg in history)
        {
            if (msg.Role == "system") continue;
            string speaker = msg.Role == "user" ? "User" : "ARI";
            sb.AppendLine($"{speaker}: {msg.Content}");
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

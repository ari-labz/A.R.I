using System.Text;
using System.Text.Json;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Memory : Agent
{
    private readonly BrainService brain;
    private readonly int          fetchDepth;
    private readonly string       brainPublicUrl;

    internal override bool QuietLogging => true;

    internal Memory(AgentConfig config, BrainService brain, int fetchDepth, string brainPublicUrl) : base(config)
    {
        this.brain          = brain;
        this.fetchDepth     = fetchDepth;
        this.brainPublicUrl = brainPublicUrl;
    }

    /// <summary>
    /// Searches Brain for notes relevant to the current conversation and incoming prompt.
    /// Runs up to <see cref="fetchDepth"/> recursive fetch steps, parallelising note retrieval
    /// within each step. Returns null if nothing relevant is found, empty string if the agent
    /// ran but found nothing (so the caller can inject "[ARI's Memories] none").
    /// </summary>
    internal async Task<string?> GetNotes(List<ThreadMessage> chatHistory, string incomingPrompt, CancellationToken ct = default)
    {
        if (fetchDepth <= 0) return null;

        List<string> allTitles = await brain.GetNoteTitles();
        if (allTitles.Count == 0) return null;

        string threadKey = $"memory:{Guid.NewGuid()}";

        if (ShouldSkip(incomingPrompt, allTitles))
        {
            Common.Logger.LogInformation("[Memory] ({Thread}) skipped", threadKey);
            return string.Empty;
        }

        string       transcript   = BuildTranscript(chatHistory, incomingPrompt);
        List<string> allPaths     = await brain.GetNotePaths();
        string       allPathsList = string.Join(", ", allPaths);

        HashSet<string>                                          fetched      = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (string Content, string? NoteId)>    noteContents = new(StringComparer.OrdinalIgnoreCase);

        string firstPrompt =
            "You are recalling memories for a conversation. " +
            "The available notes are shown as full paths — the path encodes meaning (category, ownership, relationship). " +
            "Use the paths to identify which notes are relevant without needing to fetch first.\n\n" +
            $"CONVERSATION:\n{transcript}\n\n" +
            $"AVAILABLE NOTES (full paths): {allPathsList}\n\n" +
            "Respond ONLY with JSON using bare note TITLES (the last segment of the path): " +
            "{\"fetch\": [\"[REDACT]\", \"[REDACT]'s Family\"]} — or {\"fetch\": []} if nothing is relevant.";

        string raw = await Prompt(threadKey, firstPrompt, ct: ct);

        for (int depth = 0; depth < fetchDepth; depth++)
        {
            List<string> toFetch = ParseFetchList(raw)
                .Where(n => !fetched.Contains(n))
                .ToList();

            if (toFetch.Count == 0) break;

            Common.Logger.LogInformation("[Memory] ({Thread}) requested {Notes}",
                threadKey, string.Join(", ", toFetch.Select(n => $"[{n}]")));

            IEnumerable<Task<(string name, string? content, string? noteId)>> fetchTasks = toFetch.Select(async name =>
            {
                string? content = await brain.GetNote(name);
                string? noteId  = content is not null ? await brain.GetNoteId(name) : null;
                return (name, content, noteId);
            });

            foreach ((string name, string? content, string? noteId) in await Task.WhenAll(fetchTasks))
            {
                if (content is null) continue;
                fetched.Add(name);
                noteContents[name] = (content, noteId);
            }

            if (depth + 1 >= fetchDepth) break;

            StringBuilder notesBlock = new();
            foreach (KeyValuePair<string, (string Content, string? NoteId)> kvp in noteContents)
            {
                notesBlock.AppendLine($"--- {kvp.Key} ---");
                notesBlock.AppendLine(kvp.Value.Content);
                notesBlock.AppendLine("---");
            }

            string nextPrompt =
                $"Here are the notes you requested:\n\n{notesBlock}\n" +
                $"Based on any [[links]] or references in those notes, are there further notes you want? " +
                $"Do NOT re-request notes already fetched: {string.Join(", ", fetched)}.\n" +
                "Respond ONLY with JSON: {\"fetch\": [\"Name\"]} — or {\"fetch\": []} to stop.";

            raw = await Prompt(threadKey, nextPrompt, ct: ct);
        }

        if (noteContents.Count == 0)
        {
            Common.Logger.LogInformation("[Memory] ({Thread}) complete", threadKey);
            return string.Empty;
        }

        StringBuilder result = new();
        foreach (KeyValuePair<string, (string Content, string? NoteId)> kvp in noteContents)
        {
            string url    = !string.IsNullOrEmpty(brainPublicUrl) && kvp.Value.NoteId is not null
                                ? $"{brainPublicUrl.TrimEnd('/')}/#?note={kvp.Value.NoteId}"
                                : string.Empty;
            string header = url.Length > 0 ? $"[{kvp.Key}|{url}]" : $"[{kvp.Key}]";
            result.AppendLine(header);
            result.AppendLine(kvp.Value.Content);
            result.AppendLine();
        }

        Common.Logger.LogInformation("[Memory] ({Thread}) complete", threadKey);
        return result.ToString().TrimEnd();
    }

    // ── Private ──────────────────────────────────────────────────────────────────

    private static readonly string[] MemoryKeywords =
    [
        "remember", "last time", "before", "yesterday", "you said", "we talked",
        "earlier", "previously", "used to", "told me", "you mentioned", "we discussed"
    ];

    private static readonly string[] PersonalKeywords =
    [
        " my ", "my ", "who is", "who's", "who are", "what is", "what's",
        "where is", "where's", "tell me about", "what do you know",
        "about me", "who am i", "am i ", "do you know", "know about",
        "what do i", "how do i", "what are my", "tell me"
    ];

    /// <summary>
    /// Returns true if the memory lookup can safely be skipped.
    /// Skips only when the message is short, contains no known note title,
    /// no memory-seeking keyword, and no personal-context indicator.
    /// </summary>
    private static bool ShouldSkip(string message, List<string> noteTitles)
    {
        if (message.Length >= 80) return false;
        if (message.Contains('?')) return false;

        foreach (string kw in MemoryKeywords)
            if (message.Contains(kw, StringComparison.OrdinalIgnoreCase)) return false;

        foreach (string kw in PersonalKeywords)
            if (message.Contains(kw, StringComparison.OrdinalIgnoreCase)) return false;

        foreach (string title in noteTitles)
            if (message.Contains(title, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private static string BuildTranscript(List<ThreadMessage> chatHistory, string incomingPrompt)
    {
        StringBuilder sb = new();
        foreach (ThreadMessage msg in chatHistory)
            sb.AppendLine($"{msg.Username}: {msg.Content}");
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
        return [];
    }
}

using ARI.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Memory : Agent
{
    [JsonPropertyName("recursiveBrainSearchDepth")] public int RecursiveBrainSearchDepth { get; init; }

    private const int BASE_THINKING     = 100;
    private const int PER_CANDIDATE     = 25;
    private const int MAX_THINKING      = 1000;
    private const int MIN_RECALL_LENGTH = 80;
    private const int TRANSCRIPT_LIMIT  = 5;
    private const int MAX_CANDIDATES    = 20;

    [JsonIgnore] internal BrainModule? brain          { get; set; }
    [JsonIgnore] internal string       brainPublicUrl { get; set; } = "";

    internal override bool QuietLogging => true;

    internal override ThreadType Type => ThreadType.Memory;

    internal Memory() { }

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

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of",
        "with", "by", "from", "up", "about", "into", "through", "is", "are", "was",
        "were", "be", "been", "being", "have", "has", "had", "do", "does", "did",
        "will", "would", "could", "should", "may", "might", "must", "can", "not",
        "no", "nor", "so", "yet", "both", "either", "it", "its", "this", "that",
        "these", "those", "what", "which", "who", "whom", "how", "when", "where",
        "why", "all", "any", "each", "few", "more", "most", "other", "some", "such",
        "than", "too", "very", "just", "i", "me", "my", "we", "our", "you", "your",
        "he", "she", "his", "her", "they", "their", "them", "us", "if", "as", "then",
        "also", "now", "here", "there", "out", "off", "over", "under", "again",
        "once", "dont", "doesnt", "isnt", "wasnt", "cant", "wont", "ive", "im",
        "like", "get", "got", "let", "know", "think", "want", "need", "going",
        "said", "say", "see", "look", "come", "go", "make", "take", "hey", "ari",
        "okay", "yes", "yep", "nope", "sure", "right", "yeah", "oh", "ok", "hi",
        "hello", "please", "tell", "ask", "can", "could", "would", "really", "actually",
        "maybe", "well", "bit", "lot", "one", "two", "three", "new", "old", "good",
        "bad", "big", "little", "something", "anything", "nothing", "everything",
        "someone", "anyone", "everyone", "thing", "things", "much", "many", "long",
        "time", "day", "days", "way", "use", "used", "using", "put", "set", "try"
    };

    internal async Task<string?> GetNotes(List<ThreadMessage> chatHistory, string incomingPrompt, string? contextSummary = null, CancellationToken ct = default)
    {
        if (RecursiveBrainSearchDepth <= 0) return null;

        string threadKey = $"memory:{Guid.NewGuid()}";

        // Skip for short messages with no question or memory/personal signal.
        if (incomingPrompt.Length < MIN_RECALL_LENGTH && !incomingPrompt.Contains('?'))
        {
            bool hasSignal = MemoryKeywords.Any(kw => incomingPrompt.Contains(kw, StringComparison.OrdinalIgnoreCase))
                          || PersonalKeywords.Any(kw => incomingPrompt.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (!hasSignal)
            {
                Shared.Logger.LogInformation("[Memory] skipped");
                return string.Empty;
            }
        }

        // Build transcript from last N messages.
        StringBuilder transcriptBuilder = new();
        foreach (ThreadMessage msg in chatHistory.TakeLast(TRANSCRIPT_LIMIT))
            transcriptBuilder.AppendLine($"{msg.Username}: {msg.Content}");
        string transcript = transcriptBuilder.ToString();

        // Tokenise the incoming prompt + context summary (not full transcript — avoids noisy seeds from ARI's own words).
        string combined = string.IsNullOrWhiteSpace(contextSummary)
            ? incomingPrompt
            : $"{incomingPrompt} {contextSummary}";
        HashSet<string> tokens = Regex.Split(combined, @"[^a-zA-Z0-9']+")
            .Select(t => t.Trim('\''))
            .Where(t => t.Length >= 3 && !Stopwords.Contains(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Parallel FTS per token — track how many tokens matched each note (coverage score).
        // Alias labels are searched server-side so a token like "Grumpy" surfaces "Geoffrey" here.
        List<string> tokenList = tokens.ToList();
        List<string> seeds     = new();
        Dictionary<string, int> seedCoverage = new(StringComparer.OrdinalIgnoreCase);
        if (tokenList.Count > 0)
        {
            List<string>[] searchResults = await Task.WhenAll(tokenList.Select(t => brain!.SearchNote(t)));
            for (int i = 0; i < tokenList.Count; i++)
                foreach (string title in searchResults[i])
                    seedCoverage[title] = seedCoverage.GetValueOrDefault(title) + 1;
            seeds = seedCoverage.Keys.ToList();
        }

        // One-hop expansion — track how many seeds link to each neighbour (in-degree score).
        Dictionary<string, int> neighbourPullers = new(StringComparer.OrdinalIgnoreCase);
        if (seeds.Count > 0)
        {
            List<string>[] linkResults = await Task.WhenAll(seeds.Select(t => brain!.GetNoteLinks(t)));
            for (int i = 0; i < seeds.Count; i++)
                foreach (string link in linkResults[i])
                    if (!seedCoverage.ContainsKey(link))
                        neighbourPullers[link] = neighbourPullers.GetValueOrDefault(link) + 1;
        }

        // Score every candidate and cap at MAX_CANDIDATES before showing anything to the LLM.
        // Scoring: exact title match (100) > token coverage hits (10/token) > neighbour in-degree (5/puller).
        // Exact title matches are pre-fetched without LLM involvement regardless of the cap.
        HashSet<string> tokenLower = tokenList.Select(t => t.ToLowerInvariant()).ToHashSet();

        int Score(string title)
        {
            int s = 0;
            if (tokenLower.Contains(title.ToLowerInvariant()))               s += 100;
            if (seedCoverage.TryGetValue(title, out int cov))                s += cov * 10;
            if (neighbourPullers.TryGetValue(title, out int pullers))        s += pullers * 5;
            return s;
        }

        HashSet<string> allCandidates = new(seedCoverage.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string n in neighbourPullers.Keys) allCandidates.Add(n);

        List<string> ranked     = allCandidates.OrderByDescending(Score).Take(MAX_CANDIDATES).ToList();
        List<string> directFetch = ranked.Where(c => tokenLower.Contains(c.ToLowerInvariant())).ToList();
        List<string> indirect    = ranked.Except(directFetch, StringComparer.OrdinalIgnoreCase).ToList();

        Shared.Logger.LogInformation("[Memory] tokens [{Tokens}] → {Seeds} seed(s) → ranked {Total} candidate(s) (cap {Cap}): {Direct} direct + {Indirect} indirect",
            string.Join(", ", tokenList), seeds.Count, ranked.Count, MAX_CANDIDATES, directFetch.Count, indirect.Count);

        if (ranked.Count == 0)
        {
            Shared.Logger.LogInformation("[Memory] complete 0.0s, 0 tokens, 0.0 t/s — no memories recalled");
            return string.Empty;
        }

        HashSet<string>                                       fetched      = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (string Content, string? NoteId)> noteContents = new(StringComparer.OrdinalIgnoreCase);

        // Pre-fetch direct token matches without consulting the LLM.
        if (directFetch.Count > 0)
        {
            IEnumerable<Task<(string name, string? content, string? noteId)>> directTasks = directFetch.Select(async name =>
            {
                string? content = await brain!.GetNote(name);
                string? noteId  = content is not null ? await brain!.GetNoteId(name) : null;
                return (name, content, noteId);
            });
            foreach ((string name, string? content, string? noteId) in await Task.WhenAll(directTasks))
            {
                if (content is null) continue;
                fetched.Add(name);
                noteContents[name] = (content, noteId);
            }
            Shared.Logger.LogInformation("[Memory] Direct fetch: {Notes}", string.Join(", ", fetched.Select(n => $"[{n}]")));
        }

        string candidateList = indirect.Count > 0 ? string.Join(", ", indirect) : string.Empty;

        string firstPrompt =
            "You are recalling memories for a conversation. " +
            "Fetch every note whose person, place, or topic is directly mentioned in the conversation.\n\n" +
            $"CONVERSATION:\n{transcript}\n\n" +
            $"CANDIDATE NOTES: {candidateList}\n\n" +
            "Respond ONLY with JSON using the exact note titles from the candidate list: " +
            "{\"fetch\": [\"[REDACT]\", \"[REDACT]\"]}. Only use {\"fetch\": []} if none are relevant.";

        int    totalTokens  = 0;
        double totalSeconds = 0;
        int    roundNumber  = 1;

        int thinkingBudget = Math.Min(BASE_THINKING + indirect.Count * PER_CANDIDATE, MAX_THINKING);

        Stopwatch totalTimer = Stopwatch.StartNew();

        string raw = indirect.Count > 0
            ? await SendPrompt(threadKey, firstPrompt, ct: ct, thinkingBudgetOverride: thinkingBudget)
            : "{\"fetch\": []}";

        for (int depth = 0; depth < RecursiveBrainSearchDepth; depth++)
        {
            List<string> toFetch = ParseFetchList(raw)
                .Select(n => n.Contains('/') ? n[(n.LastIndexOf('/') + 1)..] : n)
                .Where(n => !fetched.Contains(n))
                .ToList();

            if (toFetch.Count == 0)
            {
                LogRound(threadKey, roundNumber, ref totalTokens, ref totalSeconds, recalled: null);
                break;
            }

            IEnumerable<Task<(string name, string? content, string? noteId)>> fetchTasks = toFetch.Select(async name =>
            {
                string? content = await brain!.GetNote(name);
                string? noteId  = content is not null ? await brain!.GetNoteId(name) : null;
                return (name, content, noteId);
            });

            List<string> recalled = new();
            foreach ((string name, string? content, string? noteId) in await Task.WhenAll(fetchTasks))
            {
                if (content is null) continue;
                fetched.Add(name);
                recalled.Add(name);
                noteContents[name] = (content, noteId);
            }

            LogRound(threadKey, roundNumber++, ref totalTokens, ref totalSeconds, recalled);

            if (depth + 1 >= RecursiveBrainSearchDepth) break;

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

            raw = await SendPrompt(threadKey, nextPrompt, ct: ct, thinkingBudgetOverride: thinkingBudget);
        }

        totalTimer.Stop();
        double totalTokPerSec = totalSeconds > 0 ? totalTokens / totalSeconds : 0;

        if (noteContents.Count == 0)
        {
            Shared.Logger.LogInformation("[Memory] complete {Seconds}s, {Tokens} tokens, {TokPerSec} t/s — no memories recalled",
                totalTimer.Elapsed.TotalSeconds.ToString("F1"), totalTokens, totalTokPerSec.ToString("F1"));
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

        Shared.Logger.LogInformation("[Memory] complete {Seconds}s, {Tokens} tokens, {TokPerSec} t/s",
            totalTimer.Elapsed.TotalSeconds.ToString("F1"), totalTokens, totalTokPerSec.ToString("F1"));
        return result.ToString().TrimEnd();
    }

    private void LogRound(string threadKey, int round, ref int totalTokens, ref double totalSeconds, List<string>? recalled)
    {
        AriResponse? last = GetThread(threadKey)?.History.OfType<AriResponse>().LastOrDefault();
        if (last is null) return;

        int    tokens    = last.CompletionTokens;
        double elapsed   = last.ThinkingSeconds ?? 0;
        double tokPerSec = elapsed > 0 ? tokens / elapsed : 0;

        totalTokens  += tokens;
        totalSeconds += elapsed;

        if (recalled is null || recalled.Count == 0)
            Shared.Logger.LogInformation("[Memory] No memories recalled ({Tokens} tokens, {TokPerSec} t/s)",
                tokens, tokPerSec.ToString("F1"));
        else
            Shared.Logger.LogInformation("[Memory] Recalled {Notes} ({Tokens} tokens, {TokPerSec} t/s)",
                string.Join(", ", recalled.Select(n => $"[{n}]")), tokens, tokPerSec.ToString("F1"));
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

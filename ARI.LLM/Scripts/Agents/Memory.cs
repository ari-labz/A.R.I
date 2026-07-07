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
    [JsonPropertyName("hopLimit")] public int HopLimit { get; init; }

    private const int MIN_RECALL_LENGTH = 80;
    private const int TRANSCRIPT_LIMIT  = 5;
    private const int SEED_NEAR_LIMIT   = 25;
    private const int TOP_CANDIDATES    = 25;
    private const int THINKING_BUDGET   = 400;
    private const int SNIPPET_LENGTH    = 160;
    private const int MAX_SEARCH_TERMS  = 15;

    internal override bool QuietLogging => true;

    public Memory() { }

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

    // The Context agent's summary is structured (TODAY/ENTITIES/PRONOUN MAP/...); its labels and
    // date words are scaffolding, not searchable entities.
    private static readonly HashSet<string> SummaryLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "TODAY", "ENTITIES", "PRONOUN", "MAP", "PRIMARY", "TOPIC", "TOPICS", "SECONDARY",
        "HISTORY", "User", "Assistant", "None", "Started", "shifted", "updated",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
        "January", "February", "March", "April", "May", "June", "July", "August",
        "September", "October", "November", "December"
    };

    private static List<string> Tokenize(string text) => Regex.Split(text, @"[^a-zA-Z0-9']+")
        .Select(t => t.Trim('\''))
        .Where(t => t.Length >= 3 && !Stopwords.Contains(t))
        .ToList();

    internal async Task<string?> GetNotes(List<ThreadMessage> chatHistory, string incomingPrompt, string? contextSummary = null, CancellationToken ct = default)
    {
        if (HopLimit <= 0) return null;

        Thread memThread = new Thread(ThreadPipeline.Dialogue, $"memory:{Guid.NewGuid()}") { Internal = true };

        // Skip for short messages with no question or memory/personal signal.
        if (incomingPrompt.Length < MIN_RECALL_LENGTH && !incomingPrompt.Contains('?'))
        {
            bool hasSignal = MemoryKeywords.Any(kw => incomingPrompt.Contains(kw, StringComparison.OrdinalIgnoreCase))
                          || PersonalKeywords.Any(kw => incomingPrompt.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (!hasSignal)
            {
                Shared.Logger.LogInformation("[Memory] skipped");
                RunLogger.Write("Memory", "skipped", new[] { ("Recall thread", memThread) }, new[]
                {
                    $"Incoming prompt: {RunLogger.Trunc(incomingPrompt)}",
                    "Outcome: skipped — short message with no question or memory/personal signal",
                });
                return string.Empty;
            }
        }

        // Not the full transcript — avoids noisy seeds from ARI's own words. Prompt terms come
        // first so the cap trims summary terms, never the user's own words. The summary's structural
        // labels and date words are excluded — tokenising them once turned 51 terms into a 12.6s recall.
        List<string> terms = Tokenize(incomingPrompt)
            .Concat(Tokenize(contextSummary ?? string.Empty).Where(t => !SummaryLabels.Contains(t)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MAX_SEARCH_TERMS)
            .ToList();

        if (terms.Count == 0)
        {
            Shared.Logger.LogInformation("[Memory] complete 0.0s — no search terms");
            RunLogger.Write("Memory", "no-terms", new[] { ("Recall thread", memThread) }, new[]
            {
                $"Incoming prompt: {RunLogger.Trunc(incomingPrompt)}",
                "Outcome: no search terms survived tokenisation — nothing to recall",
            });
            return string.Empty;
        }

        // Pure SQL, no LLM yet — see BrainModule.Recall.
        RecallResult recall = BrainModule.Recall(terms, HopLimit, SEED_NEAR_LIMIT, TOP_CANDIDATES);

        Shared.Logger.LogInformation("[Memory] terms [{Terms}] → {Candidates} candidate(s), {Paths} path(s)",
            string.Join(", ", terms), recall.Candidates.Count, recall.Paths.Count);

        if (recall.Candidates.Count == 0)
        {
            Shared.Logger.LogInformation("[Memory] complete 0.0s, 0 tokens, 0.0 t/s — no candidates found");
            RunLogger.Write("Memory", "no-candidates", new[] { ("Recall thread", memThread) }, new[]
            {
                $"Incoming prompt: {RunLogger.Trunc(incomingPrompt)}",
                $"Context summary: {RunLogger.Trunc(contextSummary)}",
                $"Search terms: {string.Join(", ", terms)}",
                "Outcome: no candidate notes found — nothing shown to the model",
            });
            return string.Empty;
        }

        // Snippets only, highest-scored first — a well-separated top score is a fast decision even though the model always runs.
        StringBuilder transcriptBuilder = new();
        foreach (ThreadMessage msg in chatHistory.TakeLast(TRANSCRIPT_LIMIT))
            transcriptBuilder.AppendLine($"{msg.Username}: {msg.Content}");
        string transcript = transcriptBuilder.ToString();

        StringBuilder candidateBlock = new();
        foreach (SearchResult candidate in recall.Candidates)
        {
            string aliasNote = candidate.Note.Aliases.Count > 0
                ? $"(aka {string.Join(", ", candidate.Note.Aliases)}) "
                : string.Empty;
            candidateBlock.AppendLine($"- {candidate.Note.Title}: {aliasNote}{Snippet(candidate.Note.Content)}");
        }

        string pathBlock = recall.Paths.Count > 0
            ? "\nCONNECTIONS FOUND:\n" + string.Join("\n", recall.Paths.Select(p =>
                $"- {p.From.Title} connects to {p.To.Title} via {string.Join(" -> ", p.Notes.Select(n => n.Title))}"))
            : string.Empty;

        string prompt =
            "Select the notes relevant to this conversation. Prefer fewer — only what is needed to respond well. " +
            "Items are listed highest-scored first.\n\n" +
            $"CONVERSATION:\n{transcript}\n\n" +
            $"CANDIDATES (highest-scored first):\n{candidateBlock}{pathBlock}\n\n" +
            "Respond ONLY with JSON using the exact titles above: {\"select\": [\"[REDACT]\", \"[REDACT]\"]}. " +
            "Use {\"select\": []} if none are relevant.";

        Stopwatch timer = Stopwatch.StartNew();
        string raw = await SendPrompt(memThread, prompt, ct: ct, thinkingBudgetOverride: THINKING_BUDGET);
        List<string> selected = ParseSelection(raw);
        timer.Stop();

        Response? last     = memThread.History.OfType<Response>().LastOrDefault();
        int    completionTokens = last?.Data.CompletionTokens ?? 0;
        double elapsed          = last?.TotalSeconds ?? last?.ThinkingSeconds ?? 0;
        double tokPerSec        = elapsed > 0 ? completionTokens / elapsed : 0;

        if (selected.Count == 0)
        {
            Shared.Logger.LogInformation("[Memory] complete {Seconds}s, {Tokens} tokens, {TokPerSec} t/s — model selected nothing",
                timer.Elapsed.TotalSeconds.ToString("F1"), completionTokens, tokPerSec.ToString("F1"));
            RunLogger.Write("Memory", "no-recall", new[] { ("Recall thread", memThread) }, new[]
            {
                $"Incoming prompt: {RunLogger.Trunc(incomingPrompt)}",
                $"Search terms: {string.Join(", ", terms)}",
                $"Candidates offered: {recall.Candidates.Count}",
                $"Total: {timer.Elapsed.TotalSeconds:F1}s, {completionTokens} tokens, {tokPerSec:F1} t/s",
                "Outcome: model declined to select any candidate — no memories recalled",
            });
            return string.Empty;
        }

        StringBuilder result = new();
        List<string> fetched = new();
        List<string> unresolved = new();
        foreach (string title in selected)
        {
            Note? note = BrainModule.GetNote(title);
            if (note is null) { unresolved.Add(title); continue; }
            fetched.Add(note.Title);
            result.AppendLine($"[{note.Title}|{note.Url}]");
            result.AppendLine(note.ToPrompt());
            result.AppendLine();
        }
        if (unresolved.Count > 0)
            Shared.Logger.LogWarning("[Memory] model selected title(s) not found in the brain: {Unresolved}", string.Join(", ", unresolved));

        Shared.Logger.LogInformation("[Memory] complete {Seconds}s, {Tokens} tokens, {TokPerSec} t/s",
            timer.Elapsed.TotalSeconds.ToString("F1"), completionTokens, tokPerSec.ToString("F1"));
        RunLogger.Write("Memory", "recalled", new[] { ("Recall thread", memThread) }, new[]
        {
            $"Incoming prompt: {RunLogger.Trunc(incomingPrompt)}",
            $"Search terms: {string.Join(", ", terms)}",
            $"Candidates offered: {recall.Candidates.Count} · paths found: {recall.Paths.Count}",
            $"Total: {timer.Elapsed.TotalSeconds:F1}s, {completionTokens} tokens, {tokPerSec:F1} t/s",
            $"Recalled {fetched.Count} note(s): {string.Join(", ", fetched)}"
                + (unresolved.Count > 0 ? $" · unresolved: {string.Join(", ", unresolved)}" : string.Empty),
        });
        return result.ToString().TrimEnd();
    }

    // Full content is fetched only after selection, never during the deciding step.
    private static string Snippet(string content)
    {
        string line = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => !l.TrimStart().StartsWith('#'))?.Trim() ?? string.Empty;
        return line.Length > SNIPPET_LENGTH ? line[..SNIPPET_LENGTH] + "…" : line;
    }

    private static List<string> ParseSelection(string raw)
    {
        try
        {
            raw = raw.Trim();
            int start = raw.IndexOf('{');
            if (start >= 0) raw = raw[start..];
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("select", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
        }
        catch { }
        return [];
    }
}

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

    private const int TRANSCRIPT_LIMIT  = 5;
    private const int SEED_NEAR_LIMIT   = 25;
    private const int TOP_CANDIDATES    = 25;
    private const int THINKING_BUDGET   = 400;
    private const int SNIPPET_LENGTH    = 160;
    private const int MAX_SEARCH_TERMS  = 15;

    internal override bool SuppressLog() => true;

    public Memory() { }

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

    // Whole-word self-reference — the speaker is talking about themselves, so seed their own note.
    private static readonly Regex SelfReference = new(@"\b(i|me|my|myself|mine|i'?m)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<string> Tokenize(string text) => Regex.Split(text, @"[^a-zA-Z0-9']+")
        .Select(t => t.Trim('\''))
        .Where(t => t.Length >= 3 && !Stopwords.Contains(t))
        .ToList();

    internal async Task<string?> GetNotes(List<ThreadMessage> chatHistory, string incomingPrompt, string? contextSummary = null, CancellationToken ct = default)
    {
        if (HopLimit <= 0) return null;

        Thread memThread = new Thread(ThreadPipeline.Dialogue, $"memory:{Guid.NewGuid()}") { Internal = true };

        // If a brain note exists whose title or alias matches the user's name, pin its header — always
        // surface identity/pronoun facts without model selection, but only the top block, not the full note.
        string? speakerName = chatHistory.LastOrDefault(m => m.Username != "ARI")?.Username;
        Note? userNote = string.IsNullOrWhiteSpace(speakerName) ? null : BrainModule.GetNote(speakerName);
        string pinnedBlock = userNote is not null
            ? $"[{userNote.Title}|{userNote.Url}]\n{userNote.ToHeader()}\n\n"
            : string.Empty;

        // Recall always runs — it's fast enough that a keyword gate only ever costs a real hit.
        // If nothing matches, the SQL seed returns no candidates and we bail below at near-zero cost.

        // A self-referential prompt ("what do you remember about my circumstance?") carries no name
        // token, so on turn 1 — before any context summary exists — the speaker's own note never seeds.
        // When the message talks about the speaker, seed their username so recall can find that note.
        List<string> selfSeed = new();
        if (SelfReference.IsMatch(incomingPrompt))
        {
            if (!string.IsNullOrWhiteSpace(speakerName)) selfSeed.Add(speakerName);
        }

        // Not the full transcript — avoids noisy seeds from ARI's own words. Self-seed and prompt terms
        // come first so the cap trims summary terms, never the user's own words. The summary's structural
        // labels and date words are excluded — tokenising them once turned 51 terms into a 12.6s recall.
        List<string> terms = selfSeed
            .Concat(Tokenize(incomingPrompt))
            .Concat(Tokenize(contextSummary ?? string.Empty).Where(t => !SummaryLabels.Contains(t)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MAX_SEARCH_TERMS)
            .ToList();

        if (terms.Count == 0)
        {
            Shared.Logger.LogInformation("[Memory] complete 0.0s — no search terms");
            SessionRecorder.StandaloneNote("Memory", memThread.Key, "no-terms", new Dictionary<string, object?>
            {
                ["incoming_prompt"] = incomingPrompt,
                ["outcome"]         = "no search terms survived tokenisation — nothing to recall",
            });
            return string.IsNullOrEmpty(pinnedBlock) ? string.Empty : pinnedBlock.TrimEnd();
        }

        // Pure SQL, no LLM yet — see BrainModule.Recall.
        RecallResult recall = BrainModule.Recall(terms, HopLimit, SEED_NEAR_LIMIT, TOP_CANDIDATES);

        Shared.Logger.LogInformation("[Memory] terms [{Terms}] → {Candidates} candidate(s), {Paths} path(s)",
            string.Join(", ", terms), recall.Candidates.Count, recall.Paths.Count);

        if (recall.Candidates.Count == 0)
        {
            Shared.Logger.LogInformation("[Memory] complete 0.0s, 0 tokens, 0.0 t/s — no candidates found");
            SessionRecorder.StandaloneNote("Memory", memThread.Key, "no-candidates", new Dictionary<string, object?>
            {
                ["incoming_prompt"] = incomingPrompt,
                ["context_summary"] = contextSummary,
                ["search_terms"]    = terms,
                ["outcome"]         = "no candidate notes found — nothing shown to the model",
            });
            return string.IsNullOrEmpty(pinnedBlock) ? string.Empty : pinnedBlock.TrimEnd();
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

        string prompt = ResolveTemplate("HopPrompt", "",
            ("transcript", transcript),
            ("candidates", candidateBlock + pathBlock));

        Stopwatch timer = Stopwatch.StartNew();
        string raw = await Prompt(memThread, prompt, new PromptOptions { Ct = ct, ThinkingBudget = THINKING_BUDGET });
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
            SessionRecorder.StandaloneNote("Memory", memThread.Key, "no-recall", new Dictionary<string, object?>
            {
                ["incoming_prompt"]    = incomingPrompt,
                ["search_terms"]       = terms,
                ["candidates_offered"] = recall.Candidates.Count,
                ["elapsed_s"]          = Math.Round(timer.Elapsed.TotalSeconds, 3),
                ["completion_tokens"]  = completionTokens,
                ["tok_per_sec"]        = Math.Round(tokPerSec, 2),
                ["outcome"]            = "model declined to select any candidate — no memories recalled",
            });
            return string.IsNullOrEmpty(pinnedBlock) ? string.Empty : pinnedBlock.TrimEnd();
        }

        // The model is asked for exact titles but often echoes the whole decorated candidate line
        // ("Alex: (aka Al) - **Formal Name:** Alexander"). Constrain the fuzzy fallback to the notes
        // actually offered, so a mangled pick can never resolve to a note that wasn't in the list.
        HashSet<string> offered = recall.Candidates.Select(c => c.Note.Name).ToHashSet();

        StringBuilder result = new();
        List<string> fetched = new();
        List<string> fuzzy = new();
        List<string> unresolved = new();
        // Pre-seed seen with the pinned user note so the model-selected pass never emits it again.
        HashSet<string> seen = userNote is not null ? new() { userNote.Name } : new();
        foreach (string title in selected)
        {
            Note? note = Resolve(title, offered, out bool viaFuzzy);
            if (note is null) { unresolved.Add(title); continue; }
            if (!seen.Add(note.Name)) continue;
            if (viaFuzzy) fuzzy.Add(title);
            fetched.Add(note.Title);
            result.AppendLine($"[{note.Title}|{note.Url}]");
            result.AppendLine(note.ToPrompt());
            result.AppendLine();
        }
        if (fuzzy.Count > 0)
            Shared.Logger.LogInformation("[Memory] recovered decorated pick(s) via scored resolve: {Fuzzy}", string.Join(", ", fuzzy));
        if (unresolved.Count > 0)
            Shared.Logger.LogWarning("[Memory] model selected title(s) not found in the brain: {Unresolved}", string.Join(", ", unresolved));

        Shared.Logger.LogInformation("[Memory] complete {Seconds}s, {Tokens} tokens, {TokPerSec} t/s",
            timer.Elapsed.TotalSeconds.ToString("F1"), completionTokens, tokPerSec.ToString("F1"));
        SessionRecorder.StandaloneNote("Memory", memThread.Key, "recalled", new Dictionary<string, object?>
        {
            ["incoming_prompt"]    = incomingPrompt,
            ["search_terms"]       = terms,
            ["candidates_offered"] = recall.Candidates.Count,
            ["paths_found"]        = recall.Paths.Count,
            ["elapsed_s"]          = Math.Round(timer.Elapsed.TotalSeconds, 3),
            ["completion_tokens"]  = completionTokens,
            ["tok_per_sec"]        = Math.Round(tokPerSec, 2),
            ["recalled"]           = fetched,
            ["resolved_by_fuzzy"]  = fuzzy,
            ["unresolved"]         = unresolved,
        });
        return (pinnedBlock + result.ToString()).TrimEnd();
    }

    // Resolve a model's selection to a note. Fast path: exact title/alias/path via GetNote. Fallback:
    // tokenise the (often decorated) pick and run it through the scored search, keeping the top result
    // that was actually offered — title/alias tiers outweigh stray snippet words, so the intended note wins.
    private static Note? Resolve(string pick, HashSet<string> offered, out bool viaFuzzy)
    {
        viaFuzzy = false;
        Note? note = BrainModule.GetNote(pick);
        if (note is not null) return note;

        List<string> tokens = Tokenize(pick);
        if (tokens.Count == 0) return null;

        SearchResult? best = BrainModule.Search(tokens, TOP_CANDIDATES)
            .FirstOrDefault(r => offered.Contains(r.Note.Name));
        if (best is null) return null;

        viaFuzzy = true;
        return best.Note;
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

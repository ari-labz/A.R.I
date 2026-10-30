using ARI.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ARI.BrainVault;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Memory : Agent
{
    [JsonPropertyName("hopLimit")] public int HopLimit { get; init; }

    private const int TRANSCRIPT_LIMIT  = 5;
    private const int SEED_NEAR_LIMIT   = 25;
    private const int TOP_CANDIDATES    = 25;
    private const int THINKING_BUDGET   = 400;
    private const int EXTRACT_BUDGET    = 300;
    private const int SNIPPET_LENGTH    = 160;
    private const int MAX_SEARCH_TERMS  = 15;

    internal override bool SuppressLog => true;

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

    /// <summary>Everything Memory works out before it asks a model anything: the pinned note about the speaker, the search, the
    /// candidates in the form the model is shown them, and the prompt that asks it to pick. <see cref="Done"/> means there is nothing
    /// for a model to pick from, and <see cref="Early"/> is the answer.</summary>
    internal sealed record MemoryPrep(
        bool Done, string Early, string Pinned, string Transcript, string PickPrompt, IReadOnlyList<string> Offered,
        string? SpeakerNoteName, IReadOnlyList<string> Terms, int Candidates, int Paths, string ThreadKey, string IncomingPrompt, string? ContextSummary,
        string CandidateText = "", bool Disabled = false);

    /// <summary>The first half of recall: pure code, no model. See <see cref="MemoryPrep"/>.</summary>
    internal MemoryPrep Prepare(List<ThreadMessage> chatHistory, string incomingPrompt, string? contextSummary = null, PrivacyMode privacyMode = PrivacyMode.Unrestricted)
    {
        string threadKey = $"memory:{Guid.NewGuid()}";

        // If a brain note exists whose title or alias matches the user's name, pin its header — always
        // surface identity/pronoun facts without model selection, but only the top block, not the full note.
        string? speakerName = chatHistory.LastOrDefault(m => m.Username != "ARI")?.Username;
        Note? userNote = string.IsNullOrWhiteSpace(speakerName) ? null : Brain.GetNote(speakerName);
        string pinnedBlock = string.Empty;
        if (userNote is not null)
        {
            try { pinnedBlock = $"[{userNote.Title}|{userNote.Url}]\n{userNote.SensitivityNotice}{userNote.ToHeader()}\n\n"; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                WarnMissing(userNote);
                ReindexAfterMissing();
                userNote = null;
            }
        }
        string pinnedOnly() => string.IsNullOrEmpty(pinnedBlock) ? string.Empty : pinnedBlock.TrimEnd();
        MemoryPrep Finished(string early, IReadOnlyList<string> terms, int candidates = 0, int paths = 0) =>
            new(true, early, pinnedBlock, "", "", [], userNote?.Name, terms, candidates, paths, threadKey, incomingPrompt, contextSummary);

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
            SessionRecorder.StandaloneNote("Memory", threadKey, "no-terms", new Dictionary<string, object?>
            {
                ["incoming_prompt"] = incomingPrompt,
                ["outcome"]         = "no search terms survived tokenisation — nothing to recall",
            });
            return Finished(pinnedOnly(), terms);
        }

        // Pure SQL, no LLM yet — see Brain.Recall.
        RecallResult recall = Brain.Recall(terms, HopLimit, SEED_NEAR_LIMIT, TOP_CANDIDATES);

        // Guarded mode (talking to someone other than the Master): Private/ notes are never even offered
        // to the selection model, not just filtered afterward. This is deliberately a hard path check
        // rather than another judgment call — "never discuss the Master's private life with a third
        // party" should not depend on an LLM getting a borderline case right every single time.
        List<SearchResult> candidates = privacyMode == PrivacyMode.Guarded
            ? recall.Candidates.Where(c => !c.Note.Path.StartsWith("Private/", StringComparison.OrdinalIgnoreCase)).ToList()
            : recall.Candidates.ToList();

        Shared.Logger.LogInformation("[Memory] terms [{Terms}] → {Candidates} candidate(s), {Paths} path(s)",
            string.Join(", ", terms), candidates.Count, recall.Paths.Count);

        if (candidates.Count == 0)
        {
            Shared.Logger.LogInformation("[Memory] complete 0.0s, 0 tokens, 0.0 t/s — no candidates found");
            SessionRecorder.StandaloneNote("Memory", threadKey, "no-candidates", new Dictionary<string, object?>
            {
                ["incoming_prompt"] = incomingPrompt,
                ["context_summary"] = contextSummary,
                ["search_terms"]    = terms,
                ["outcome"]         = "no candidate notes found — nothing shown to the model",
            });
            return Finished(pinnedOnly(), terms);
        }

        // Snippets only, highest-scored first — a well-separated top score is a fast decision even though the model always runs.
        StringBuilder transcriptBuilder = new();
        foreach (ThreadMessage msg in chatHistory.TakeLast(TRANSCRIPT_LIMIT))
            transcriptBuilder.AppendLine($"{msg.Username}: {msg.Content}");
        string transcript = transcriptBuilder.ToString();

        StringBuilder candidateBlock = new();
        bool missingNote = false;
        List<SearchResult> readable = new();
        foreach (SearchResult candidate in candidates)
        {
            string aliasNote = candidate.Note.Aliases.Count > 0
                ? $"(aka {string.Join(", ", candidate.Note.Aliases)}) "
                : string.Empty;
            string snippet;
            try { snippet = Snippet(candidate.Note.Content); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                WarnMissing(candidate.Note);
                missingNote = true;
                continue;
            }
            candidateBlock.AppendLine($"- {candidate.Note.Title}: {aliasNote}{snippet}");
            readable.Add(candidate);
        }
        candidates = readable;
        if (missingNote) ReindexAfterMissing();
        if (candidates.Count == 0)
            return Finished(pinnedOnly(), terms);

        string pathBlock = recall.Paths.Count > 0
            ? "\nCONNECTIONS FOUND:\n" + string.Join("\n", recall.Paths.Select(p =>
                $"- {p.From.Title} connects to {p.To.Title} via {string.Join(" -> ", p.Notes.Select(n => n.Title))}"))
            : string.Empty;

        // The model is asked for exact titles but often echoes the whole decorated candidate line
        // ("Alex: (aka Al) - **Formal Name:** Alexander"). Constrain the fuzzy fallback to the notes
        // actually offered, so a mangled pick can never resolve to a note that wasn't in the list.
        string prompt = ResolveTemplate("HopPrompt", "",
            ("transcript", transcript),
            ("candidates", candidateBlock + pathBlock));

        return new MemoryPrep(false, "", pinnedBlock, transcript, prompt, candidates.Select(c => c.Note.Name).ToList(),
            userNote?.Name, terms, candidates.Count, recall.Paths.Count, threadKey, incomingPrompt, contextSummary, candidateBlock + pathBlock);
    }

    /// <summary>How a model step is run, so the same second half of recall can use the Memory agent itself (the default) or the
    /// agents a pipeline wires in.</summary>
    internal sealed record MemoryModelSteps(Func<string, string, string, Task<string>> Extract, Func<string, string, Task<string>> FilterPrivacy);

    /// <summary>The second half of recall: reads the model's picks, pulls the relevant lines out of each chosen note (one model
    /// call per note, in parallel), puts the block together and, in guarded mode, has it checked for privacy.</summary>
    internal async Task<string> Finish(string pickRaw, MemoryPrep prep, PrivacyMode privacyMode, MemoryModelSteps? steps, CancellationToken ct,
        Stopwatch? timer = null, int completionTokens = 0, double tokPerSec = 0)
    {
        timer ??= Stopwatch.StartNew();
        string incomingPrompt = prep.IncomingPrompt;
        string transcript = prep.Transcript;
        string pinnedBlock = prep.Pinned;
        IReadOnlyList<string> terms = prep.Terms;
        List<string> selected = ParseSelection(pickRaw);

        if (selected.Count == 0)
        {
            Shared.Logger.LogInformation("[Memory] complete {Seconds}s, {Tokens} tokens, {TokPerSec} t/s — model selected nothing",
                timer.Elapsed.TotalSeconds.ToString("F1"), completionTokens, tokPerSec.ToString("F1"));
            SessionRecorder.StandaloneNote("Memory", prep.ThreadKey, "no-recall", new Dictionary<string, object?>
            {
                ["incoming_prompt"]    = incomingPrompt,
                ["search_terms"]       = terms,
                ["candidates_offered"] = prep.Candidates,
                ["elapsed_s"]          = Math.Round(timer.Elapsed.TotalSeconds, 3),
                ["completion_tokens"]  = completionTokens,
                ["tok_per_sec"]        = Math.Round(tokPerSec, 2),
                ["outcome"]            = "model declined to select any candidate — no memories recalled",
            });
            return string.IsNullOrEmpty(pinnedBlock) ? string.Empty : pinnedBlock.TrimEnd();
        }

        HashSet<string> offered = prep.Offered.ToHashSet();

        List<string> fetched = new();
        List<string> fuzzy = new();
        List<string> unresolved = new();
        // Pre-seed seen with the pinned user note so the model-selected pass never emits it again.
        HashSet<string> seen = prep.SpeakerNoteName is not null ? new() { prep.SpeakerNoteName } : new();
        List<(Note Note, bool ViaFuzzy)> resolved = new();
        foreach (string title in selected)
        {
            Note? note = Resolve(title, offered, out bool viaFuzzy);
            if (note is null) { unresolved.Add(title); continue; }
            if (!seen.Add(note.Name)) continue;
            try { _ = note.Content; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                WarnMissing(note);
                ReindexAfterMissing();
                continue;
            }
            if (viaFuzzy) fuzzy.Add(title);
            fetched.Add(note.Title);
            resolved.Add((note, viaFuzzy));
        }

        // Extract only the relevant lines from each selected note in parallel.
        Task<string>[] extractionTasks = resolved.Select(r => ExtractRelevant(r.Note, incomingPrompt, transcript, ct, steps?.Extract)).ToArray();
        string[] extractions = await Task.WhenAll(extractionTasks);

        StringBuilder result = new();
        for (int i = 0; i < resolved.Count; i++)
        {
            Note note = resolved[i].Note;
            string extracted = extractions[i];
            result.AppendLine($"[{note.Title}|{note.Url}]");
            // The extraction sub-call may not carry a whole-note sensitivity flag through into its summary
            // (it never saw one — that flag lives in frontmatter, stripped out of what it was shown), so
            // it's re-applied here rather than trusted to survive the extraction step.
            result.Append(note.SensitivityNotice);
            result.AppendLine(string.IsNullOrWhiteSpace(extracted) ? note.ToPrompt() : $"Path: {note.Name}\n\n{extracted}");
            result.AppendLine();
        }
        if (fuzzy.Count > 0)
            Shared.Logger.LogInformation("[Memory] recovered decorated pick(s) via scored resolve: {Fuzzy}", string.Join(", ", fuzzy));
        if (unresolved.Count > 0)
            Shared.Logger.LogWarning("[Memory] model selected title(s) not found in the brain: {Unresolved}", string.Join(", ", unresolved));

        Shared.Logger.LogInformation("[Memory] complete {Seconds}s, {Tokens} tokens, {TokPerSec} t/s",
            timer.Elapsed.TotalSeconds.ToString("F1"), completionTokens, tokPerSec.ToString("F1"));
        SessionRecorder.StandaloneNote("Memory", prep.ThreadKey, "recalled", new Dictionary<string, object?>
        {
            ["incoming_prompt"]    = incomingPrompt,
            ["search_terms"]       = terms,
            ["candidates_offered"] = prep.Candidates,
            ["paths_found"]        = prep.Paths,
            ["elapsed_s"]          = Math.Round(timer.Elapsed.TotalSeconds, 3),
            ["completion_tokens"]  = completionTokens,
            ["tok_per_sec"]        = Math.Round(tokPerSec, 2),
            ["recalled"]           = fetched,
            ["resolved_by_fuzzy"]  = fuzzy,
            ["unresolved"]         = unresolved,
        });
        string combined = (pinnedBlock + result.ToString()).TrimEnd();

        if (privacyMode == PrivacyMode.Guarded && !string.IsNullOrWhiteSpace(combined))
        {
            Shared.Logger.LogInformation("[Memory] privacy filter: guarded mode — filtering recalled notes.");
            combined = await FilterForPrivacy(combined, transcript, ct, steps?.FilterPrivacy);
        }

        return combined;
    }

    internal async Task<string?> GetNotes(List<ThreadMessage> chatHistory, string incomingPrompt, string? contextSummary = null, CancellationToken ct = default, PrivacyMode privacyMode = PrivacyMode.Unrestricted)
    {
        if (HopLimit <= 0) return null;

        MemoryPrep prep = Prepare(chatHistory, incomingPrompt, contextSummary, privacyMode);
        if (prep.Done) return prep.Early;

        Thread memThread = new Thread(ThreadPipeline.Dialogue, prep.ThreadKey) { Internal = true };
        Stopwatch timer = Stopwatch.StartNew();
        string raw = await Prompt(memThread, prep.PickPrompt, new PromptOptions { Ct = ct, ThinkingBudget = THINKING_BUDGET });
        timer.Stop();

        Response? last     = memThread.History.OfType<Response>().LastOrDefault();
        int    completionTokens = last?.Data.CompletionTokens ?? 0;
        double elapsed          = last?.TotalSeconds ?? last?.ThinkingSeconds ?? 0;
        double tokPerSec        = elapsed > 0 ? completionTokens / elapsed : 0;

        return await Finish(raw, prep, privacyMode, steps: null, ct, timer, completionTokens, tokPerSec);
    }

    private const int PRIVACY_BUDGET = 400;

    private async Task<string> FilterForPrivacy(string recallBlock, string transcript, CancellationToken ct, Func<string, string, Task<string>>? run = null)
    {
        try
        {
            if (run is not null)
            {
                string viaStep = await run(recallBlock, transcript);
                if (string.IsNullOrWhiteSpace(viaStep)) return recallBlock;
                Shared.Logger.LogInformation("[Memory] privacy filter applied.");
                return viaStep.Trim();
            }
            Thread filterThread = new Thread(ThreadPipeline.Dialogue, $"privacy-filter:{Guid.NewGuid()}") { Internal = true };
            string prompt = ResolveTemplate("PrivacyFilter", PrivacyFilterFallback,
                ("memories", recallBlock),
                ("transcript", transcript));
            string filtered = await Prompt(filterThread, prompt, new PromptOptions { Ct = ct, ThinkingBudget = PRIVACY_BUDGET });
            if (string.IsNullOrWhiteSpace(filtered)) return recallBlock;
            Shared.Logger.LogInformation("[Memory] privacy filter applied.");
            return filtered.Trim();
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning(ex, "[Memory] privacy filter failed — passing unfiltered (safe: guarded context will still have platform-level instructions).");
            return recallBlock;
        }
    }

    internal const string PrivacyFilterFallback =
        "You are ARI's privacy filter. You are in a GUARDED context — the person you are talking to is NOT your master.\n\n" +
        "Below are memories recalled for this conversation. Review each one and decide:\n" +
        "- KEEP: the memory is safe to share (general knowledge, shared projects, non-sensitive facts)\n" +
        "- OMIT: the memory contains private, sensitive, or intimate information about the master\n\n" +
        "For each memory you keep, reproduce it exactly as-is. For memories you omit, do not include them.\n" +
        "At the end, add a line: \"[Privacy: N memory/memories omitted — not appropriate to share in this context]\"\n" +
        "If all memories are safe, reproduce them all with no privacy line.\n\n" +
        "Use the keywords on each note to guide your judgement. Keywords like intimate, health, legal, financial, mental-health, identity, relationship " +
        "are strong signals that the memory is private. Keywords like gaming, technology, work (in shared project contexts) are usually safe.\n\n" +
        "MEMORIES:\n{memories}\n\nCONVERSATION:\n{transcript}\n\nOutput ONLY the filtered memory block.";

    // Resolve a model's selection to a note. Fast path: exact title/alias/path via GetNote. Fallback:
    // tokenise the (often decorated) pick and run it through the scored search, keeping the top result
    // that was actually offered — title/alias tiers outweigh stray snippet words, so the intended note wins.
    private static Note? Resolve(string pick, HashSet<string> offered, out bool viaFuzzy)
    {
        viaFuzzy = false;
        Note? note = Brain.GetNote(pick);
        if (note is not null) return note;

        List<string> tokens = Tokenize(pick);
        if (tokens.Count == 0) return null;

        SearchResult? best = Brain.Search(tokens, TOP_CANDIDATES)
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

    private async Task<string> ExtractRelevant(Note note, string request, string transcript, CancellationToken ct, Func<string, string, string, Task<string>>? run = null)
    {
        try
        {
            if (run is not null) return await run(request, transcript, note.ToPrompt());
            Thread extractThread = new Thread(ThreadPipeline.Dialogue, $"extract:{Guid.NewGuid()}") { Internal = true };
            string prompt = ResolveTemplate("ExtractPrompt", "",
                ("request", request),
                ("transcript", transcript),
                ("note_content", note.ToPrompt()));
            return await Prompt(extractThread, prompt, new PromptOptions { Ct = ct, ThinkingBudget = EXTRACT_BUDGET });
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning(ex, "[Memory] extraction failed for '{Title}', falling back to full content", note.Title);
            return string.Empty;
        }
    }

    private static void WarnMissing(Note note) =>
        Shared.Logger.LogWarning("[Memory] note '{Title}' is in the index but its file is missing ({Path}) — skipping it and re-indexing.", note.Title, note.Path);

    private static int reindexing;

    // Runs once at a time in the background so the recall carries on without waiting for the rebuild.
    private static void ReindexAfterMissing()
    {
        if (Interlocked.Exchange(ref reindexing, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try { Brain.Index(); }
            catch (Exception ex) { Shared.Logger.LogWarning("[Memory] re-index after a missing note failed: {Err}", ex.Message); }
            finally { Interlocked.Exchange(ref reindexing, 0); }
        });
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

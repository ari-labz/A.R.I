using System.Text;
using System.Text.Json;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

// Shared base for Engram and Refactor — both write to the same brain, both must avoid duplicate
// notes at 100k-note scale, both can now think as long as they want (background slot, nothing
// user-facing waits on them) as long as CONTEXT doesn't grow unbounded across a sweep. The two
// agents differ in trigger and gather logic (Engram: one conversation transcript; Refactor: a
// folder/cluster of existing notes) so they keep their own Run/RunEngram entry points, but share:
// gather -> resolve candidates (search-then-judge, never a full-title dump) -> plan -> batched
// write -> apply. Each phase forks a FRESH thread seeded only with the previous phase's conclusion,
// not its reasoning trace, so a long think on phase 1 never compounds into phase 2/3/4.
internal abstract class BrainAgent : Agent
{
    // Runs on a background slot, so it can think — but 6000 was overkill (~100s sweeps) for the small,
    // well-scoped decisions each phase makes. 2500 keeps room to reason without the runaway cost.
    protected const int THINKING_BUDGET = 2500;

    // Bounded candidate list for dedup — never the whole vault.
    private const int JUDGE_CANDIDATE_LIMIT = 8;

    // Chunk size for batched writes — bounds one call's context regardless of plan size.
    protected const int WRITE_BATCH_SIZE = 12;

    internal record EntityMention(string Name, IReadOnlyList<string> Terms, string Context, bool IsSpeaker = false);
    internal record CandidatePlan(EntityMention Mention, Note? ExistingNote);

    // An exact title (100) or exact alias (90) hit is definitionally the same note — no judge needed.
    private const double EXACT_MATCH_SCORE = 90.0;
    internal record BrainPlanItem(string Op, string Name, string Summary, string? NewName = null);

    // ── Shared: dedup via Search, never a full-title dump (GitHub #121/#26) ─────────────

    protected async Task<List<CandidatePlan>> ResolveCandidates(IReadOnlyList<EntityMention> mentions)
    {
        List<CandidatePlan> plans = new();
        foreach (EntityMention mention in mentions)
        {
            List<SearchResult> candidates = BrainModule.Search(mention.Terms, JUDGE_CANDIDATE_LIMIT);
            if (candidates.Count == 0) { plans.Add(new CandidatePlan(mention, null)); continue; }

            // Skip the judge when the answer is unambiguous: an exact title/alias hit is the same note,
            // and the speaker is always their own person note (even on a fuzzy username→name match).
            // The judge — the weak link — only decides genuinely fuzzy, non-speaker matches.
            if (mention.IsSpeaker || candidates[0].Score >= EXACT_MATCH_SCORE)
            {
                plans.Add(new CandidatePlan(mention, candidates[0].Note));
                continue;
            }

            Thread judgeThread = NewPhaseThread("brain-judge");
            string raw = await SendPrompt(judgeThread, JudgePrompt(mention, candidates), thinkingBudgetOverride: THINKING_BUDGET);
            string? sameAs = ParseJudgment(raw);
            Note? match = sameAs is null
                ? null
                : candidates.FirstOrDefault(c => string.Equals(c.Note.Title, sameAs, StringComparison.OrdinalIgnoreCase))?.Note;
            plans.Add(new CandidatePlan(mention, match));
        }
        return plans;
    }

    private static string JudgePrompt(EntityMention mention, List<SearchResult> candidates)
    {
        StringBuilder sb = new();
        sb.AppendLine($"Is \"{mention.Name}\" ({mention.Context}) the same entity as any of these existing notes?");
        sb.AppendLine();
        sb.AppendLine("CANDIDATES:");
        foreach (SearchResult candidate in candidates)
        {
            string aliasNote = candidate.Note.Aliases.Count > 0 ? $" (aka: {string.Join(", ", candidate.Note.Aliases)})" : string.Empty;
            string snippet = FirstLine(candidate.Note.Content);
            sb.AppendLine($"- {candidate.Note.Title}{aliasNote}: {snippet}");
        }
        sb.AppendLine();
        sb.AppendLine("Output ONLY: {\"same_as\": \"ExactTitleFromAbove\"} if one of them is genuinely the " +
                       "same entity, or {\"same_as\": null} if this is something new. Do not force a match — " +
                       "a coincidental name match on a different entity should be \"null\".");
        return sb.ToString();
    }

    private static string FirstLine(string content) =>
        content.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => !l.TrimStart().StartsWith('#'))?.Trim() ?? string.Empty;

    private static string? ParseJudgment(string raw)
    {
        try
        {
            raw = raw.Trim();
            int start = raw.IndexOf('{');
            if (start >= 0) raw = raw[start..];
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("same_as", out JsonElement el)) return null;
            return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        }
        catch { return null; }
    }

    // ── Shared: batched write, chunked so one call's context can't blow up ─────────────

    protected async Task<BrainWriter.ApplyResult> WritePlan(
        List<BrainPlanItem> plan,
        string rulesPreamble,
        CancellationToken ct)
    {
        Dictionary<string, string> summaryByTitle = plan.ToDictionary(BareTitle, p => p.Summary, StringComparer.OrdinalIgnoreCase);
        List<EngramAdd> allAdds = new();
        List<EngramEdit> allEdits = new();
        List<EngramDelete> allDeletes = new();
        List<EngramMerge> allMerges = new();
        List<EngramThought> allThoughts = new();

        StringBuilder sweepSummary = new();
        foreach (List<BrainPlanItem> batch in Chunk(plan, WRITE_BATCH_SIZE))
        {
            Thread writeThread = NewPhaseThread("brain-write");
            string prompt = WritePrompt(rulesPreamble, batch, sweepSummary.ToString());
            string raw = await SendPrompt(writeThread, prompt, ct: ct, maxTokensOverride: -1, thinkingBudgetOverride: THINKING_BUDGET);

            BrainWriter.ParsedBatch parsed = BrainWriter.Parse(raw);
            allAdds.AddRange(parsed.Adds);
            allEdits.AddRange(parsed.Edits);
            allDeletes.AddRange(parsed.Deletes);
            allMerges.AddRange(parsed.Merges);
            allThoughts.AddRange(parsed.Thoughts);

            foreach (BrainPlanItem item in batch)
                sweepSummary.AppendLine($"- {item.Name}: {item.Summary}");
        }

        return BrainWriter.Apply(new BrainWriter.ParsedBatch(allAdds, allEdits, allDeletes, allMerges, allThoughts), summaryByTitle);
    }

    private static string WritePrompt(string rulesPreamble, List<BrainPlanItem> batch, string savedSoFar)
    {
        StringBuilder sb = new();
        sb.AppendLine(rulesPreamble);
        sb.AppendLine();
        if (savedSoFar.Length > 0)
        {
            sb.AppendLine("Notes already saved this sweep:");
            sb.Append(savedSoFar);
            sb.AppendLine();
        }
        sb.AppendLine("Write full content for every note in this batch:");
        foreach (BrainPlanItem item in batch)
        {
            string move = string.IsNullOrWhiteSpace(item.NewName) ? string.Empty : $" (move to {item.NewName})";
            sb.AppendLine($"- {item.Op}: {item.Name}{move} — {item.Summary}");
        }
        sb.AppendLine();
        sb.AppendLine("Rules for every note:");
        sb.AppendLine("- Title must be the everyday name, never a role or status.");
        sb.AppendLine("- LINKS USE BARE TITLES ONLY: write [[[REDACT]]], NEVER [[People/[REDACT]]] or any path form.");
        sb.AppendLine("- WEAVE links into the relevant sentence or bullet (e.g. \"- **Boyfriend:** [[[REDACT]]]\"). " +
                       "NEVER dump links as loose lines at the end of the note.");
        sb.AppendLine("- Every note MUST link outward to its hub, plus one link for every other entity it mentions " +
                       "that exists or is being created this sweep. When EDITING, keep every [[link]] the existing " +
                       "content already has — dropping links disconnects the graph.");
        sb.AppendLine("- Only [[link]] to notes that exist or are being created this sweep.");
        sb.AppendLine("- Every Events entry needs a specific or approximate date.");
        sb.AppendLine("- Include ## Changelog with a dated entry. No [[links]] in changelog.");
        sb.AppendLine("- Include an \"aliases\" array — every nickname/alt-name someone might say aloud.");
        sb.AppendLine("- If you notice something worth recording as a thought (see THOUGHTS rule above), include it. " +
                       "A thought's spanText MUST be a verbatim line or bullet copied from the note content you are " +
                       "writing in THIS response — never a line from the conversation.");
        sb.AppendLine();
        sb.AppendLine("Output ONLY:");
        sb.AppendLine("{\"add\": [{\"name\": \"...\", \"content\": \"markdown\", \"aliases\": [...]}],");
        sb.AppendLine(" \"edit\": [{\"name\": \"...\", \"newName\": \"...\", \"content\": \"markdown\", \"aliases\": [...]}],");
        sb.AppendLine(" \"merge\": [{\"from\": \"...\", \"into\": \"...\", \"reason\": \"...\"}],");
        sb.AppendLine(" \"delete\": [{\"name\": \"...\", \"reason\": \"...\"}],");
        sb.AppendLine(" \"thoughts\": [{\"note\": \"...\", \"spanText\": \"verbatim line from that note's content above\",");
        sb.AppendLine("                \"comment\": \"...\", \"confidence\": \"low|medium|high\", \"kind\": \"observation|self-prompt\"}]}");
        return sb.ToString();
    }

    // ── Shared helpers ───────────────────────────────────────────────────────────────

    protected static Thread NewPhaseThread(string label) => new Thread(ThreadPipeline.Dialogue, $"{label}:{Guid.NewGuid()}") { Internal = true };

    protected static string BareTitle(BrainPlanItem item) =>
        BareName(string.IsNullOrWhiteSpace(item.NewName) ? item.Name : item.NewName);

    protected static string BareName(string path) => path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

    // Chunks a list into bounded batches so no single write call's context grows with plan size.
    protected static IEnumerable<List<T>> Chunk<T>(List<T> items, int size)
    {
        for (int i = 0; i < items.Count; i += size)
            yield return items.GetRange(i, Math.Min(size, items.Count - i));
    }
}

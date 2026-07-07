using ARI.Common;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

// Refactor is "re-run Engram over the whole brain": no new conversation to gather from, but a
// chance to notice structural duplicates, dead ends, and patterns across many notes at once —
// exactly the whole-folder visibility a single-conversation sweep (Engram) never has. Full-graph
// scale (100k notes in one working set) is a known limitation, deferred by design for now — this
// pass only shares plumbing (parsing/apply via BrainWriter, rules via BrainRulebook) and adds
// thought-recording, not a scale rewrite.
internal class Refactor : BrainAgent
{
    [JsonIgnore] internal Engram?      engram { get; set; }

    private readonly SemaphoreSlim runLock = new(1, 1);

    private const int SINGLE_PASS_THRESHOLD = 15;
    private const int CLUSTER_CALL_LIMIT    = 20;
    private const int EXCERPT_LENGTH        = 300;

    public Refactor() { }

    // Incremental pass: processes dirty notes + their 1-hop references, expanded to full folders.
    // Full pass (/refactor all): processes every note in the graph. Backs up the brain first in both modes.
    internal async Task<string> Run(bool allNotes = false)
    {
        if (!await runLock.WaitAsync(TimeSpan.FromSeconds(5)))
            return "Refactor skipped — already running.";

        // Pause Engram for the duration of the refactor so a sweep mid-pass cannot
        // write notes that conflict with the changes being applied. Restore its
        // previous state (enabled or disabled) when done.
        bool engramWasEnabled = engram?.IsEnabled ?? false;
        if (engramWasEnabled)
        {
            engram!.Disable();
            Shared.Logger.LogInformation("[Refactor] Engram paused for refactor.");
        }

        try
        {
            Shared.Logger.LogInformation("[Refactor] Starting {Mode} pass.", allNotes ? "full" : "incremental");

            // ── Backup ────────────────────────────────────────────────────────────
            string backupResult = BrainModule.Backup();
            Shared.Logger.LogInformation("[Refactor] {Backup}", backupResult);

            // ── Clean duplicate Unknown stubs ─────────────────────────────────────
            int stubsDeleted = BrainModule.CleanUnknownStubs();
            if (stubsDeleted > 0)
                Shared.Logger.LogInformation("[Refactor] Deleted {Count} duplicate Unknown stub(s).", stubsDeleted);

            // ── Seed ──────────────────────────────────────────────────────────────
            List<string> seedTitles = allNotes
                ? BrainModule.GetTitles()
                : BrainModule.GetDirtyNotes();

            if (seedTitles.Count == 0)
                return allNotes
                    ? "Brain is empty — nothing to refactor."
                    : "No dirty notes — graph is up to date. Use `/refactor all` for a full scan.";

            Shared.Logger.LogInformation("[Refactor] Seed: {Count} note(s).", seedTitles.Count);

            // ── Load seed notes ───────────────────────────────────────────────────
            Dictionary<string, NoteData> loaded = new(StringComparer.OrdinalIgnoreCase);

            async Task Load(string title)
            {
                if (loaded.ContainsKey(title)) return;
                string? raw = BrainModule.GetNote(title)?.ToPrompt();
                if (raw is not null) loaded[title] = ParseNoteData(title, raw);
            }

            foreach (string t in seedTitles) await Load(t);

            // ── 1-hop expansion ───────────────────────────────────────────────────
            List<string> outbound = loaded.Values.SelectMany(d => d.Links)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (string t in outbound) await Load(t);

            // ── Expand to full folders ────────────────────────────────────────────
            // Cluster detection needs to see every note in a touched folder,
            // not just the dirty subset, so the LLM has complete context.
            HashSet<string> touchedFolders = loaded.Values
                .Select(d => TopFolder(d.Folder))
                .Where(f => !string.IsNullOrEmpty(f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (string folder in touchedFolders)
            {
                foreach (string t in BrainModule.GetTitlesByFolder(folder))
                    await Load(t);
            }

            // Load root-level hub notes regardless (LLM needs to know what hubs exist)
            foreach (string t in BrainModule.GetTitlesByFolder(string.Empty))
                await Load(t);

            Shared.Logger.LogInformation("[Refactor] Working set: {Count} note(s) across folder(s): {Folders}.",
                loaded.Count, string.Join(", ", touchedFolders));

            // ── All titles (lightweight, from cache) ──────────────────────────────
            List<string> allTitles = BrainModule.GetTitles();

            // ── Strip See Also sections + orphan link bullets (thoughts survive — CarryThoughtsInto
            //    at apply time re-anchors them against whatever the LLM edit ultimately produces) ──
            HashSet<string> knownTitlesSet = new(allTitles, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, EngramEdit> seeAlsoEdits = new(StringComparer.OrdinalIgnoreCase);
            foreach (string title in loaded.Keys.ToList())
            {
                NoteData data   = loaded[title];
                string stripped = data.Markdown;

                stripped = Regex.Replace(stripped,
                    @"^## See Also\b.*?(?=^##|\z)", string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline);
                stripped = Regex.Replace(stripped, @"\n{3,}", "\n\n").Trim();

                stripped = Regex.Replace(stripped,
                    @"^[ \t]*[-*]\s+\[\[([^\]]+)\]\]\s*$",
                    match => knownTitlesSet.Contains(match.Groups[1].Value.Trim()) ? match.Value : string.Empty,
                    RegexOptions.Multiline);
                stripped = Regex.Replace(stripped, @"\n{3,}", "\n\n").Trim();

                stripped = Regex.Replace(stripped,
                    @"(^## Changelog\b.*?)((?=^##)|\z)",
                    m => Regex.Replace(m.Value, @"\[\[([^\]]+)\]\]", "$1"),
                    RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline);
                if (stripped == data.Markdown) continue;

                loaded[title] = data with { Markdown = stripped, Links = ParseLinks(stripped) };
                seeAlsoEdits[title] = new EngramEdit { NoteName = title, Content = stripped };
                Shared.Logger.LogInformation("[Refactor] Stripped See Also / orphan bullets from '{Title}'.", title);
            }

            if (seeAlsoEdits.Count > 0)
                Shared.Logger.LogInformation("[Refactor] Pre-processed {Count} note(s) (See Also strip + orphan bullet removal).", seeAlsoEdits.Count);

            // ── Group by top-level folder ─────────────────────────────────────────
            List<NoteData> hubNotes = loaded.Values.Where(d => string.IsNullOrEmpty(d.Folder)).ToList();
            Dictionary<string, List<NoteData>> byFolder = loaded.Values
                .Where(d => !string.IsNullOrEmpty(d.Folder))
                .GroupBy(d => TopFolder(d.Folder), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // ── Analyse each folder ───────────────────────────────────────────────
            BrainWriter.ParsedBatch combined = new([], [], [], [], []);
            Dictionary<string, string> summaryByTitle = new(StringComparer.OrdinalIgnoreCase);

            foreach ((string folder, List<NoteData> notes) in byFolder)
            {
                Shared.Logger.LogInformation("[Refactor] Analysing folder '{Folder}' ({Count} note(s)).", folder, notes.Count);

                List<BrainWriter.ParsedBatch> results = notes.Count <= SINGLE_PASS_THRESHOLD
                    ? [await AnalyseFolder(folder, notes, hubNotes, allTitles)]
                    : await AnalyseLarge(folder, notes, hubNotes, allTitles);

                foreach (BrainWriter.ParsedBatch result in results)
                    combined = Merge(combined, result);
            }

            (List<EngramAdd> allAdds, List<EngramEdit> allEdits, List<EngramDelete> allDeletes, List<EngramMerge> allMerges) =
                Dedupe(combined);

            // ── Merge and apply operations ────────────────────────────────────────
            // LLM edits take priority: they already saw stripped content, so their output
            // is a superset of the See Also removal.
            Dictionary<string, EngramEdit> mergedEdits = new(seeAlsoEdits, StringComparer.OrdinalIgnoreCase);
            foreach (EngramEdit edit in allEdits) mergedEdits[edit.NoteName] = edit;
            List<EngramEdit> finalEdits = mergedEdits.Values.ToList();

            BrainWriter.ApplyResult applied = BrainWriter.Apply(
                new BrainWriter.ParsedBatch(allAdds, finalEdits, allDeletes, allMerges, combined.Thoughts),
                summaryByTitle);

            // Terminal guardrail: de-link any reference that resolves to nothing after the pass.
            int delinked = BrainModule.StripUnresolvedLinks();
            if (delinked > 0) Shared.Logger.LogInformation("[Refactor] de-linked unresolved references in {Count} note(s).", delinked);

            // ── Hub child-link pass ───────────────────────────────────────────────
            // Deterministic: every hub (folder) links to each direct child, including child hubs.
            // Done after all structural changes so it reflects the final folder tree.
            int hubsLinked = BrainModule.EnsureHubChildLinks();
            if (hubsLinked > 0)
                Shared.Logger.LogInformation("[Refactor] Hub child-link pass updated {Count} hub(s).", hubsLinked);

            // ── Clear dirty flags ─────────────────────────────────────────────────
            BrainModule.ClearDirty(loaded.Keys);

            StringBuilder summary = new();
            summary.AppendLine($"Refactor {(allNotes ? "full" : "incremental")} complete — " +
                $"{allAdds.Count} added, {finalEdits.Count} edited, {allMerges.Count} merged, {allDeletes.Count} deleted, " +
                $"{combined.Thoughts.Count} thought(s) recorded ({applied.Failed} failed).");
            if (!allNotes) summary.AppendLine($"Working set: {seedTitles.Count} dirty note(s) expanded to {loaded.Count} across [{string.Join(", ", touchedFolders)}].");
            if (seeAlsoEdits.Count > 0) summary.AppendLine($"See Also sections removed: {seeAlsoEdits.Count}.");
            return summary.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            Shared.Logger.LogError("[Refactor] Failed: {Message}", ex.Message);
            return $"Refactor failed: {ex.Message}";
        }
        finally
        {
            if (engramWasEnabled)
            {
                engram!.Enable();
                Shared.Logger.LogInformation("[Refactor] Engram restored.");
            }
            runLock.Release();
        }
    }

    // ── Folder analysis ───────────────────────────────────────────────────────────

    // Single-call analysis for folders with ≤ SINGLE_PASS_THRESHOLD notes.
    private async Task<BrainWriter.ParsedBatch> AnalyseFolder(string folder, List<NoteData> notes, List<NoteData> hubNotes, List<string> allTitles)
    {
        Thread refactorThread = NewPhaseThread($"refactor-folder-{folder}");
        string prompt         = BuildFolderPrompt(folder, notes, hubNotes, allTitles);

        Shared.Logger.LogInformation("[Refactor] Folder '{Folder}': single-pass LLM call ({Count} notes).", folder, notes.Count);
        string raw = await SendPrompt(refactorThread, prompt, maxTokensOverride: -1, thinkingBudgetOverride: THINKING_BUDGET);
        BrainWriter.ParsedBatch parsed = BrainWriter.Parse(raw);

        // Enforce folder scope: only accept edits for notes that live inside this folder.
        // This prevents cross-folder edits (e.g. Relationships pass rewriting Events/ notes).
        // Merges are exempt — folding a duplicate (e.g. Unknown/[REDACT]) into a canonical note in
        // another folder is exactly what a merge is for.
        List<EngramEdit> scopedEdits = parsed.Edits.Where(e =>
        {
            string path = e.NoteName;
            string editFolder = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
            return string.IsNullOrEmpty(folder)
                || string.Equals(editFolder, folder, StringComparison.OrdinalIgnoreCase)
                || editFolder.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }).ToList();

        return parsed with { Edits = scopedEdits };
    }

    // Two-pass analysis for folders exceeding the single-pass threshold.
    // Pass 1: summary view → cluster plan. Pass 2: one analysis call per cluster with full note content.
    private async Task<List<BrainWriter.ParsedBatch>> AnalyseLarge(string folder, List<NoteData> notes, List<NoteData> hubNotes, List<string> allTitles)
    {
        Thread p1Thread = NewPhaseThread($"refactor-clusters-{folder}");
        string p1Prompt = ClusterDetectionPrompt(folder, notes, hubNotes);

        Shared.Logger.LogInformation("[Refactor] Folder '{Folder}': cluster detection pass ({Count} notes).", folder, notes.Count);
        string clusterRaw          = await SendPrompt(p1Thread, p1Prompt, maxTokensOverride: -1, thinkingBudgetOverride: THINKING_BUDGET);
        List<ClusterPlan> clusters = ParseClusterPlan(clusterRaw);

        if (clusters.Count == 0)
        {
            Shared.Logger.LogInformation("[Refactor] Folder '{Folder}': no clusters identified — skipping.", folder);
            return [];
        }

        Shared.Logger.LogInformation("[Refactor] Folder '{Folder}': {Count} cluster(s) identified.", folder, clusters.Count);

        List<BrainWriter.ParsedBatch> results = new();
        Dictionary<string, NoteData> notesByTitle = notes.ToDictionary(n => n.Title, StringComparer.OrdinalIgnoreCase);

        foreach (ClusterPlan cluster in clusters)
        {
            List<NoteData> clusterNotes = cluster.Members
                .Where(m => notesByTitle.ContainsKey(m))
                .Select(m => notesByTitle[m])
                .Take(CLUSTER_CALL_LIMIT)
                .ToList();

            if (clusterNotes.Count == 0) continue;

            NoteData? existingHub = hubNotes.FirstOrDefault(h => string.Equals(h.Title, cluster.HubName, StringComparison.OrdinalIgnoreCase));

            Thread p2Thread = NewPhaseThread($"refactor-cluster-{cluster.Theme}");
            string p2Prompt = ClusterAnalysisPrompt(cluster, clusterNotes, existingHub, hubNotes, allTitles);

            Shared.Logger.LogInformation("[Refactor] Cluster '{Theme}' ({Count} notes).", cluster.Theme, clusterNotes.Count);
            string raw = await SendPrompt(p2Thread, p2Prompt, maxTokensOverride: -1, thinkingBudgetOverride: THINKING_BUDGET);
            BrainWriter.ParsedBatch parsed = BrainWriter.Parse(raw);

            // Enforce cluster scope: only accept edits for notes that are members of this cluster.
            HashSet<string> memberTitles = clusterNotes.Select(n => n.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<EngramEdit> scopedEdits = parsed.Edits.Where(e => memberTitles.Contains(BareName(e.NoteName))).ToList();

            results.Add(parsed with { Edits = scopedEdits });
        }

        return results;
    }

    // ── Combining parsed batches across folder/cluster passes ────────────────────────

    private static BrainWriter.ParsedBatch Merge(BrainWriter.ParsedBatch a, BrainWriter.ParsedBatch b) => new(
        [..a.Adds, ..b.Adds], [..a.Edits, ..b.Edits], [..a.Deletes, ..b.Deletes], [..a.Merges, ..b.Merges], [..a.Thoughts, ..b.Thoughts]);

    // Multiple folder passes can emit operations for the same note. Edits take priority over adds
    // for the same bare title; within each list last-writer-wins. An edit's effective title is its
    // newName (if set) because that is the title the note will have after the operation.
    private static (List<EngramAdd>, List<EngramEdit>, List<EngramDelete>, List<EngramMerge>) Dedupe(BrainWriter.ParsedBatch combined)
    {
        Dictionary<string, EngramEdit> editsByTitle = new(StringComparer.OrdinalIgnoreCase);
        foreach (EngramEdit edit in combined.Edits)
        {
            string effective = string.IsNullOrWhiteSpace(edit.NewNoteName) ? BareName(edit.NoteName) : BareName(edit.NewNoteName);
            editsByTitle[effective] = edit;
        }

        Dictionary<string, EngramAdd> addsByTitle = new(StringComparer.OrdinalIgnoreCase);
        foreach (EngramAdd add in combined.Adds) addsByTitle[BareName(add.NoteName)] = add;
        foreach (string title in editsByTitle.Keys) addsByTitle.Remove(title);

        Dictionary<string, EngramDelete> deletesByTitle = new(StringComparer.OrdinalIgnoreCase);
        foreach (EngramDelete del in combined.Deletes) deletesByTitle.TryAdd(BareName(del.NoteName), del);

        // Deduplicate and sanity-check merges. Drop self-merges, duplicate sources, and any merge
        // that would fold away a note already chosen as a winner — this collapses the contradictory
        // A→B + B→A pair an LLM can emit into a single coherent direction. A note being merged away
        // is removed from the delete list — MergeNotes deletes it itself.
        Dictionary<string, EngramMerge> mergesByFrom = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> mergeWinners = new(StringComparer.OrdinalIgnoreCase);
        foreach (EngramMerge mg in combined.Merges)
        {
            string from = BareName(mg.From), into = BareName(mg.Into);
            if (string.Equals(from, into, StringComparison.OrdinalIgnoreCase)) continue;
            if (mergesByFrom.ContainsKey(from)) continue;
            if (mergeWinners.Contains(from)) continue;
            mergesByFrom[from] = mg;
            mergeWinners.Add(into);
        }

        List<EngramDelete> finalDeletes = deletesByTitle.Values
            .Where(d => !mergesByFrom.ContainsKey(BareName(d.NoteName)) && !mergeWinners.Contains(BareName(d.NoteName)))
            .ToList();

        return (addsByTitle.Values.ToList(), editsByTitle.Values.ToList(), finalDeletes, mergesByFrom.Values.ToList());
    }

    // ── Prompt builders ───────────────────────────────────────────────────────────

    private static string BuildFolderPrompt(string folder, List<NoteData> notes, List<NoteData> hubNotes, List<string> allTitles)
    {
        StringBuilder sb = new();
        sb.AppendLine($"Analyse the `{folder}` folder and restructure the graph.");
        sb.AppendLine();
        AppendHubsBlock(sb, hubNotes);
        AppendNotesBlock(sb, notes, full: true);
        AppendTitlesBlock(sb, allTitles);
        AppendInstructions(sb);
        return sb.ToString();
    }

    private static string ClusterDetectionPrompt(string folder, List<NoteData> notes, List<NoteData> hubNotes)
    {
        StringBuilder sb = new();
        sb.AppendLine($"Identify thematic clusters in the `{folder}` folder.");
        sb.AppendLine();
        AppendHubsBlock(sb, hubNotes);
        AppendNotesBlock(sb, notes, full: false);
        sb.AppendLine("Output ONLY raw JSON — no fences:");
        sb.AppendLine("{");
        sb.AppendLine("  \"clusters\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"theme\": \"Family\",");
        sb.AppendLine("      \"hub_name\": \"[REDACT]'s Family\",");
        sb.AppendLine("      \"hub_exists\": true,");
        sb.AppendLine("      \"members\": [\"Ryan\", \"[REDACT]\", \"Peter\"]");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string ClusterAnalysisPrompt(ClusterPlan cluster, List<NoteData> clusterNotes, NoteData? existingHub, List<NoteData> allHubNotes, List<string> allTitles)
    {
        StringBuilder sb = new();
        sb.AppendLine($"Analyse the `{cluster.Theme}` cluster and make structural changes.");
        sb.AppendLine();
        if (existingHub is not null)
        {
            sb.AppendLine("EXISTING HUB:");
            AppendNote(sb, existingHub, full: true);
        }
        AppendHubsBlock(sb, allHubNotes.Where(h => h.Title != existingHub?.Title).ToList());
        AppendNotesBlock(sb, clusterNotes, full: true);
        AppendTitlesBlock(sb, allTitles);
        AppendInstructions(sb);
        return sb.ToString();
    }

    private static void AppendHubsBlock(StringBuilder sb, List<NoteData> hubNotes)
    {
        if (hubNotes.Count == 0) return;
        sb.AppendLine("EXISTING HUBS:");
        foreach (NoteData hub in hubNotes) AppendNote(sb, hub, full: true);
        sb.AppendLine();
    }

    private static void AppendNotesBlock(StringBuilder sb, List<NoteData> notes, bool full)
    {
        sb.AppendLine(full ? "NOTES (full content):" : "NOTES (title + excerpt for cluster detection):");
        foreach (NoteData note in notes) AppendNote(sb, note, full);
        sb.AppendLine();
    }

    private static void AppendNote(StringBuilder sb, NoteData note, bool full)
    {
        string path = string.IsNullOrEmpty(note.Folder) ? note.Title : $"{note.Folder}/{note.Title}";
        sb.AppendLine($"--- {path} ---");
        sb.AppendLine(full ? note.Markdown : (note.Markdown.Length > EXCERPT_LENGTH ? note.Markdown[..EXCERPT_LENGTH] + "…" : note.Markdown));
        sb.AppendLine("---");
    }

    private static void AppendTitlesBlock(StringBuilder sb, List<string> allTitles)
    {
        sb.AppendLine("ALL EXISTING NOTE TITLES (use for link validation — only link to titles in this list):");
        sb.AppendLine(string.Join(", ", allTitles));
        sb.AppendLine();
    }

    private static void AppendInstructions(StringBuilder sb)
    {
        sb.AppendLine(BrainRulebook.RULES);
        sb.AppendLine();
        sb.AppendLine("REFACTOR-SPECIFIC RULES:");
        sb.AppendLine("1. MERGE DUPLICATES — if two notes describe the SAME entity, pick ONE canonical note and fold " +
                       "the others into it via 'merge' ({\"from\": loser, \"into\": winner}). Put the winner's combined " +
                       "content in 'edit'. NEVER 'delete' a duplicate (that loses its links) — merge it. Emit each merge " +
                       "in ONE direction only.");
        sb.AppendLine("2. BROKEN LINKS — if a [[link]] target is not in ALL EXISTING NOTE TITLES: rename it to the " +
                       "correct title if obvious, otherwise delete it. If the broken link is the whole content of a " +
                       "bullet, delete the bullet.");
        sb.AppendLine("3. STAY IN YOUR FOLDER — only emit EDITS for notes whose path begins with the folder you are " +
                       "analysing. (Merges and adds may cross folders.) Cross-folder edits cause content loss when " +
                       "passes run in parallel.");
        sb.AppendLine("4. UNKNOWN FOLDER — notes in Unknown/ with a clear home move (via newName) to the right folder; " +
                       "if an Unknown/ note duplicates a real note, MERGE it; empty or descriptor-only stubs go in 'delete'.");
        sb.AppendLine("5. THIS IS YOUR BEST CHANCE TO NOTICE STRUCTURAL PATTERNS — you see this whole folder/cluster at " +
                       "once, unlike a single-conversation sweep. If you notice something across multiple notes (a " +
                       "recurring topic, an unresolved thread, thin coverage of a person), record it as a thought.");
        sb.AppendLine();
        sb.AppendLine("PRESERVE CONTENT — do not invent or alter facts. Restructure links, titles, paths and hubs, and merge duplicates; keep the facts intact.");
        sb.AppendLine();
        sb.AppendLine("OUTPUT FORMAT — respond with ONLY raw JSON, starting immediately with {. No explanation, no preamble, no reasoning:");
        sb.AppendLine("{ \"add\": [{ \"name\": \"Folder/NoteName\", \"content\": \"full markdown\", \"aliases\": [\"AltName\"] }], " +
                       "\"edit\": [{ \"name\": \"EXACT current path from the --- header --- above\", \"newName\": \"NewFolder/NoteName\", " +
                       "\"content\": \"full markdown\", \"aliases\": [\"AltName\"] }], " +
                       "\"merge\": [{ \"from\": \"DuplicateTitle\", \"into\": \"CanonicalTitle\", \"reason\": \"same person\" }], " +
                       "\"delete\": [{ \"name\": \"NoteName\", \"reason\": \"brief reason\" }], " +
                       "\"thoughts\": [{ \"note\": \"NoteName\", \"spanText\": \"verbatim line from that note's content above\", " +
                       "\"comment\": \"...\", \"confidence\": \"low|medium|high\", \"kind\": \"observation|self-prompt\" }] }");
        sb.AppendLine("Rules: (a) An edit's 'name' MUST exactly match the path in the note's --- header --- (e.g. 'People/Family/Jake', " +
                       "not 'People/Jake'). (b) Omit newName if the note does not move. (c) Use 'merge' (bare titles) for two notes " +
                       "that are the same entity; use 'delete' ONLY for descriptor-only or empty notes — never delete a duplicate. " +
                       "(d) If nothing is needed: { \"add\": [], \"edit\": [], \"merge\": [], \"delete\": [], \"thoughts\": [] }");
        sb.AppendLine();
        sb.AppendLine("/no_think");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private record NoteData(string Title, string Folder, string Markdown, HashSet<string> Links);
    private record ClusterPlan(string Theme, string HubName, bool HubExists, List<string> Members);

    private static NoteData ParseNoteData(string title, string raw)
    {
        int sep    = raw.IndexOf("\n\n", StringComparison.Ordinal);
        string md  = sep >= 0 ? raw[(sep + 2)..] : raw;
        string path = sep >= 0 && raw.StartsWith("Path: ", StringComparison.Ordinal) ? raw[6..sep] : title;
        string folder = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
        return new NoteData(title, folder, md, ParseLinks(md));
    }

    private static HashSet<string> ParseLinks(string markdown)
        => Regex.Matches(markdown, @"\[\[([^\]]+)\]\]")
                .Select(m => m.Groups[1].Value.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string TopFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return string.Empty;
        int slash = folder.IndexOf('/');
        return slash >= 0 ? folder[..slash] : folder;
    }

    // ── JSON parsers (cluster plan is Refactor-specific; add/edit/delete/merge/thoughts share BrainWriter) ──

    private static List<ClusterPlan> ParseClusterPlan(string raw)
    {
        raw = BrainWriter.StripFences(raw);
        int start = raw.IndexOf('{');
        if (start < 0) return [];

        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(raw[start..]);
            if (!doc.RootElement.TryGetProperty("clusters", out System.Text.Json.JsonElement arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                return [];

            List<ClusterPlan> clusters = new();
            foreach (System.Text.Json.JsonElement el in arr.EnumerateArray())
            {
                string? theme   = el.TryGetProperty("theme", out System.Text.Json.JsonElement t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null;
                string? hubName = el.TryGetProperty("hub_name", out System.Text.Json.JsonElement h) && h.ValueKind == System.Text.Json.JsonValueKind.String ? h.GetString() : null;
                List<string> members = el.TryGetProperty("members", out System.Text.Json.JsonElement ma) && ma.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? ma.EnumerateArray().Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String).Select(e => e.GetString()!).ToList()
                    : [];

                if (!string.IsNullOrWhiteSpace(theme) && !string.IsNullOrWhiteSpace(hubName))
                    clusters.Add(new ClusterPlan(theme, hubName, true, members));
            }
            return clusters;
        }
        catch { return []; }
    }
}

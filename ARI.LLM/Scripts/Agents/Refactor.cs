using ARI.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Refactor : Agent
{
    [JsonIgnore] internal BrainModule? brain  { get; set; }
    [JsonIgnore] internal Engram?      engram { get; set; }

    private readonly SemaphoreSlim runLock = new(1, 1);

    private const int SINGLE_PASS_THRESHOLD = 15;
    private const int CLUSTER_CALL_LIMIT    = 20;
    private const int EXCERPT_LENGTH        = 300;

    public Refactor() { }

    /// <summary>
    /// Incremental pass: processes dirty notes + their 1-hop references, expanded to full folders.
    /// Full pass (/refactor all): processes every note in the graph.
    /// Backs up the brain first in both modes.
    /// </summary>
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
            string backupResult = await brain!.Backup();
            Shared.Logger.LogInformation("[Refactor] {Backup}", backupResult);

            // ── Clean duplicate Unknown stubs ─────────────────────────────────────
            int stubsDeleted = await brain!.CleanUnknownStubs();
            if (stubsDeleted > 0)
                Shared.Logger.LogInformation("[Refactor] Deleted {Count} duplicate Unknown stub(s).", stubsDeleted);

            // ── Seed ──────────────────────────────────────────────────────────────
            List<string> seedTitles = allNotes
                ? await brain!.GetNoteTitles()
                : await brain!.GetDirtyNotes();

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
                string? raw = await brain!.GetNote(title);
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
                foreach (string t in brain!.GetTitlesByFolder(folder))
                    await Load(t);
            }

            // Load root-level hub notes regardless (LLM needs to know what hubs exist)
            foreach (string t in brain!.GetTitlesByFolder(string.Empty))
                await Load(t);

            Shared.Logger.LogInformation("[Refactor] Working set: {Count} note(s) across folder(s): {Folders}.",
                loaded.Count, string.Join(", ", touchedFolders));

            // ── All titles (lightweight, from cache) ──────────────────────────────
            List<string> allTitles = await brain!.GetNoteTitles();

            // ── Strip See Also sections + orphan link bullets ─────────────────────
            // Do this before analysis so the LLM receives clean content.
            // Collect stripped notes as edits; they'll be merged with LLM edits at apply time.
            HashSet<string> knownTitlesSet = new(allTitles, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, EngramEdit> seeAlsoEdits = new(StringComparer.OrdinalIgnoreCase);
            foreach (string title in loaded.Keys.ToList())
            {
                NoteData data   = loaded[title];
                string stripped = data.Markdown;

                // Remove ## See Also section
                stripped = Regex.Replace(stripped,
                    @"^## See Also\b.*?(?=^##|\z)", string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline);
                stripped = Regex.Replace(stripped, @"\n{3,}", "\n\n").Trim();

                // Remove bullet lines whose only content is a [[link]] to a non-existent note
                stripped = Regex.Replace(stripped,
                    @"^[ \t]*[-*]\s+\[\[([^\]]+)\]\]\s*$",
                    match => knownTitlesSet.Contains(match.Groups[1].Value.Trim()) ? match.Value : string.Empty,
                    RegexOptions.Multiline);
                stripped = Regex.Replace(stripped, @"\n{3,}", "\n\n").Trim();

                // Remove [[wiki links]] from inside ## Changelog sections
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
            List<NoteData> hubNotes = loaded.Values
                .Where(d => string.IsNullOrEmpty(d.Folder))
                .ToList();

            Dictionary<string, List<NoteData>> byFolder = loaded.Values
                .Where(d => !string.IsNullOrEmpty(d.Folder))
                .GroupBy(d => TopFolder(d.Folder), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // ── Analyse each folder ───────────────────────────────────────────────
            List<EngramAdd>    allAdds    = new();
            List<EngramEdit>   allEdits   = new();
            List<EngramDelete> allDeletes = new();
            List<EngramMerge>  allMerges  = new();

            foreach ((string folder, List<NoteData> notes) in byFolder)
            {
                Shared.Logger.LogInformation("[Refactor] Analysing folder '{Folder}' ({Count} note(s)).", folder, notes.Count);

                List<(List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes, List<EngramMerge> merges)> results;

                if (notes.Count <= SINGLE_PASS_THRESHOLD)
                {
                    // Small folder — one call handles everything
                    (List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes, List<EngramMerge> merges) = await AnalyseFolder(folder, notes, hubNotes, allTitles);
                    results = [(adds, edits, deletes, merges)];
                }
                else
                {
                    // Large folder — detect clusters first, then analyse each cluster
                    results = await AnalyseLarge(folder, notes, hubNotes, allTitles);
                }

                foreach ((List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes, List<EngramMerge> merges) in results)
                {
                    allAdds.AddRange(adds);
                    allEdits.AddRange(edits);
                    allDeletes.AddRange(deletes);
                    allMerges.AddRange(merges);
                }
            }

            // ── Deduplicate across folder passes ──────────────────────────────────
            // Multiple folder passes can emit operations for the same note. Edits take
            // priority over adds for the same bare title; within each list last-writer-wins.
            // An edit's effective title is its newName (if set) because that is the title
            // the note will have after the operation — e.g. editing People/Family with
            // newName People/[REDACT]'s Family has effective title "[REDACT]'s Family".
            Dictionary<string, EngramEdit> editsByTitle = new(StringComparer.OrdinalIgnoreCase);
            foreach (EngramEdit edit in allEdits)
            {
                string effective = string.IsNullOrWhiteSpace(edit.NewNoteName)
                    ? BareTitle(edit.NoteName)
                    : BareTitle(edit.NewNoteName);
                editsByTitle[effective] = edit; // last-writer-wins
            }

            Dictionary<string, EngramAdd> addsByTitle = new(StringComparer.OrdinalIgnoreCase);
            foreach (EngramAdd add in allAdds)
                addsByTitle[BareTitle(add.NoteName)] = add; // last-writer-wins

            // Remove adds whose effective title is already covered by an edit.
            foreach (string title in editsByTitle.Keys)
                addsByTitle.Remove(title);

            // Deduplicate deletes by title (first-writer-wins is fine here).
            Dictionary<string, EngramDelete> deletesByTitle = new(StringComparer.OrdinalIgnoreCase);
            foreach (EngramDelete del in allDeletes)
                deletesByTitle.TryAdd(BareTitle(del.NoteName), del);

            allAdds    = addsByTitle.Values.ToList();
            allEdits   = editsByTitle.Values.ToList();
            allDeletes = deletesByTitle.Values.ToList();

            // Deduplicate and sanity-check merges. Drop self-merges, duplicate sources, and any
            // merge that would fold away a note already chosen as a winner — this collapses the
            // contradictory A→B + B→A pair an LLM can emit into a single coherent direction.
            // A note being merged away is removed from the delete list — MergeNotes deletes it
            // itself, after repointing its inbound links and folding its name into the winner.
            Dictionary<string, EngramMerge> mergesByFrom = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> mergeWinners = new(StringComparer.OrdinalIgnoreCase);
            foreach (EngramMerge mg in allMerges)
            {
                string from = BareTitle(mg.From), into = BareTitle(mg.Into);
                if (string.Equals(from, into, StringComparison.OrdinalIgnoreCase)) continue; // self-merge
                if (mergesByFrom.ContainsKey(from)) continue;                                  // duplicate source
                if (mergeWinners.Contains(from)) continue;                                     // would merge away a winner (A↔B / chains)
                mergesByFrom[from] = mg;
                mergeWinners.Add(into);
            }
            allMerges  = mergesByFrom.Values.ToList();
            // Never delete a note that a merge touches — neither a source (MergeNotes deletes it
            // itself) nor a WINNER (deleting it would destroy the just-merged content).
            allDeletes = allDeletes.Where(d =>
                !mergesByFrom.ContainsKey(BareTitle(d.NoteName)) &&
                !mergeWinners.Contains(BareTitle(d.NoteName))).ToList();

            // ── Merge and apply operations ────────────────────────────────────────
            // LLM edits take priority: they already saw stripped content, so their output
            // is a superset of the See Also removal.
            Dictionary<string, EngramEdit> mergedEdits = new(seeAlsoEdits, StringComparer.OrdinalIgnoreCase);
            foreach (EngramEdit edit in allEdits)
                mergedEdits[edit.NoteName] = edit;

            List<EngramEdit> finalEdits = mergedEdits.Values.ToList();

            if (allAdds.Count > 0)
            {
                Shared.Logger.LogInformation("[Refactor] Applying {Count} add(s).", allAdds.Count);
                await brain!.AddNotes(allAdds);
            }

            if (finalEdits.Count > 0)
            {
                Shared.Logger.LogInformation("[Refactor] Applying {Count} edit(s) ({SeeAlso} See Also strip(s), {Llm} LLM edit(s)).",
                    finalEdits.Count, seeAlsoEdits.Count, allEdits.Count);
                await brain!.EditNotes(finalEdits);
            }

            // Merges run after edits (so the winner's combined content is already written) and
            // before deletes. Each fold aliases the loser's name onto the winner, repoints inbound
            // links, then deletes the loser.
            if (allMerges.Count > 0)
            {
                Shared.Logger.LogInformation("[Refactor] Applying {Count} merge(s).", allMerges.Count);
                foreach (EngramMerge mg in allMerges)
                {
                    Shared.Logger.LogInformation("[Refactor] Merging '{From}' → '{Into}': {Reason}", mg.From, mg.Into, mg.Reason);
                    try { await brain!.MergeNotes(mg.From, mg.Into); }
                    catch (Exception ex) { Shared.Logger.LogWarning("[Refactor] Merge '{From}' → '{Into}' failed: {Message}", mg.From, mg.Into, ex.Message); }
                }
            }

            if (allDeletes.Count > 0)
            {
                Shared.Logger.LogInformation("[Refactor] Applying {Count} delete(s).", allDeletes.Count);
                foreach (EngramDelete del in allDeletes)
                {
                    Shared.Logger.LogInformation("[Refactor] Deleting '{Name}': {Reason}", del.NoteName, del.Reason);
                    try { await brain!.DeleteNote(del.NoteName); }
                    catch (Exception ex) { Shared.Logger.LogWarning("[Refactor] Delete '{Name}' failed: {Message}", del.NoteName, ex.Message); }
                }
            }

            // ── Hub child-link pass ───────────────────────────────────────────────
            // Deterministic: every hub (folder) links to each direct child, including child hubs.
            // Done after all structural changes so it reflects the final folder tree.
            int hubsLinked = await brain!.EnsureHubChildLinks();
            if (hubsLinked > 0)
                Shared.Logger.LogInformation("[Refactor] Hub child-link pass updated {Count} hub(s).", hubsLinked);

            // ── Clear dirty flags ─────────────────────────────────────────────────
            await brain!.ClearDirty(loaded.Keys);

            StringBuilder summary = new();
            summary.AppendLine($"Refactor {(allNotes ? "full" : "incremental")} complete — {allAdds.Count} added, {finalEdits.Count} edited, {allMerges.Count} merged, {allDeletes.Count} deleted.");
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

    /// <summary>
    /// Single-call analysis for folders with ≤ SinglePassThreshold notes.
    /// The LLM receives all note content and existing hubs, and outputs adds + edits + deletes.
    /// </summary>
    private async Task<(List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes, List<EngramMerge> merges)> AnalyseFolder(
        string folder,
        List<NoteData> notes,
        List<NoteData> hubNotes,
        List<string> allTitles)
    {
        Thread refactorThread = new Thread(ThreadPipeline.Dialogue, $"refactor-folder-{folder}:{Guid.NewGuid()}") { Internal = true };
        string prompt         = BuildFolderPrompt(folder, notes, hubNotes, allTitles);

        Shared.Logger.LogInformation("[Refactor] Folder '{Folder}': single-pass LLM call ({Count} notes).", folder, notes.Count);
        string raw = await SendPrompt(refactorThread, prompt, maxTokensOverride: -1);
        (List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes, List<EngramMerge> merges) = ParseAddEdit(raw);

        // Enforce folder scope: only accept edits for notes that live inside this folder.
        // This prevents cross-folder edits (e.g. Relationships pass rewriting Events/ notes).
        // Merges are exempt — folding a duplicate (e.g. Unknown/[REDACT]) into a canonical note in
        // another folder is exactly what a merge is for.
        edits = edits.Where(e =>
        {
            string path = e.NoteName;
            string editFolder = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
            return string.IsNullOrEmpty(folder)
                || string.Equals(editFolder, folder, StringComparison.OrdinalIgnoreCase)
                || editFolder.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }).ToList();

        return (adds, edits, deletes, merges);
    }

    /// <summary>
    /// Two-pass analysis for folders exceeding the single-pass threshold.
    /// Pass 1: summary view → cluster plan.
    /// Pass 2: one analysis call per cluster with full note content.
    /// </summary>
    private async Task<List<(List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes, List<EngramMerge> merges)>> AnalyseLarge(
        string folder,
        List<NoteData> notes,
        List<NoteData> hubNotes,
        List<string> allTitles)
    {
        // Pass 1 — Cluster detection (titles + short excerpt per note)
        Thread p1Thread = new Thread(ThreadPipeline.Dialogue, $"refactor-clusters-{folder}:{Guid.NewGuid()}") { Internal = true };
        string p1Prompt = ClusterDetectionPrompt(folder, notes, hubNotes);

        Shared.Logger.LogInformation("[Refactor] Folder '{Folder}': cluster detection pass ({Count} notes).", folder, notes.Count);
        string clusterRaw     = await SendPrompt(p1Thread, p1Prompt, maxTokensOverride: -1);
        List<ClusterPlan> clusters = ParseClusterPlan(clusterRaw);

        if (clusters.Count == 0)
        {
            Shared.Logger.LogInformation("[Refactor] Folder '{Folder}': no clusters identified — skipping.", folder);
            return [];
        }

        Shared.Logger.LogInformation("[Refactor] Folder '{Folder}': {Count} cluster(s) identified.", folder, clusters.Count);

        // Pass 2 — One analysis call per cluster
        List<(List<EngramAdd>, List<EngramEdit>, List<EngramDelete>, List<EngramMerge>)> results = new();
        Dictionary<string, NoteData> notesByTitle = notes.ToDictionary(n => n.Title, StringComparer.OrdinalIgnoreCase);

        foreach (ClusterPlan cluster in clusters)
        {
            List<NoteData> clusterNotes = cluster.Members
                .Where(m => notesByTitle.ContainsKey(m))
                .Select(m => notesByTitle[m])
                .Take(CLUSTER_CALL_LIMIT)
                .ToList();

            if (clusterNotes.Count == 0) continue;

            NoteData? existingHub = hubNotes.FirstOrDefault(h =>
                string.Equals(h.Title, cluster.HubName, StringComparison.OrdinalIgnoreCase));

            Thread p2Thread = new Thread(ThreadPipeline.Dialogue, $"refactor-cluster-{cluster.Theme}:{Guid.NewGuid()}") { Internal = true };
            string p2Prompt = ClusterAnalysisPrompt(cluster, clusterNotes, existingHub, hubNotes, allTitles);

            Shared.Logger.LogInformation("[Refactor] Cluster '{Theme}' ({Count} notes).", cluster.Theme, clusterNotes.Count);
            string raw = await SendPrompt(p2Thread, p2Prompt, maxTokensOverride: -1);
            (List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes, List<EngramMerge> merges) = ParseAddEdit(raw);

            // Enforce cluster scope: only accept edits for notes that are members of this cluster.
            // Adds (new hub notes) and merges (cross-folder folds) are always allowed. This prevents
            // later cluster passes from overwriting content written by earlier passes out of scope.
            HashSet<string> memberTitles = clusterNotes.Select(n => n.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
            edits = edits.Where(e => memberTitles.Contains(BareTitle(e.NoteName))).ToList();

            results.Add((adds, edits, deletes, merges));
        }

        return results;
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

    private static string ClusterAnalysisPrompt(
        ClusterPlan cluster,
        List<NoteData> clusterNotes,
        NoteData? existingHub,
        List<NoteData> allHubNotes,
        List<string> allTitles)
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
        if (full)
            sb.AppendLine(note.Markdown);
        else
            sb.AppendLine(note.Markdown.Length > EXCERPT_LENGTH ? note.Markdown[..EXCERPT_LENGTH] + "…" : note.Markdown);
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
        sb.AppendLine("HOW THE GRAPH IS READ (why this matters):");
        sb.AppendLine("- A note's TITLE is its identity. The whole store is keyed by unique title, so one entity may have exactly ONE note. Two notes for the same person/place/thing — even under slightly different titles ('[REDACT]' vs '[REDACT] (Boyfriend)') — is the worst defect. Fix it with a merge, never by leaving both.");
        sb.AppendLine("- Recall reaches a note, then follows that note's OWN outward [[links]] to find what is related. Inbound links are invisible to recall. So a note with no outward link is a dead end: findable, but it leads nowhere. Every entity must link outward to its hub.");
        sb.AppendLine();
        sb.AppendLine("YOUR TASKS:");
        sb.AppendLine("1. MERGE DUPLICATES — if two notes describe the SAME entity (e.g. '[REDACT] (Boyfriend)' and '[REDACT]'; '[REDACT] and [REDACT]' and '[REDACT] and [REDACT] Relationship'; an Unknown/ stub and its real note), pick ONE canonical note and fold the others into it via the 'merge' list ({\"from\": loser, \"into\": winner}). Merging folds the loser's name into the winner's aliases, repoints inbound links, and deletes the loser — so nothing breaks. Put the winner's combined content in 'edit'. NEVER leave two notes for one entity, and NEVER 'delete' a duplicate (that loses its links) — merge it. Emit each merge in ONE direction only — never both A→B and B→A for the same pair.");
        sb.AppendLine("2. NO DEAD ENDS — every entity note must contain at least one outward [[link]] to its hub (a family member → its family hub; a device → the owner's tech hub). If a note has no outward links, add the hub link. Members link UP to the hub; the hub links DOWN to each member.");
        sb.AppendLine("3. HUBS — one hub per cluster. A hub links to EVERY one of its DIRECT children, including children that are themselves hubs: '[REDACT]'s Family' must link to 'Immediate Family', 'Cousins', and 'Grandparents'. Link to direct children only, NEVER grandchildren — the parent hub links to each sub-hub, and each sub-hub links to its own members. A person links to their top-level hubs (Family, Friends, Romantic Partners, Tech), not to individuals the sub-hubs already route. Hubs that belong to a person are named possessively ('[REDACT]'s Family'). NEVER merge a sub-hub that has its own member notes into its parent — that is a real nesting level, not a duplicate; keep it and link to it. Merge only flat, childless duplicate hubs (e.g. a stray '[REDACT]'s Cousins' into the 'Cousins' folder-hub).");
        sb.AppendLine("4. PRUNE OVER-CONNECTION — every edge must have a reason: hub membership, the subject of a fact on this note, or hub-to-member indexing. Remove links that exist for no reason (a laptop linked to a friend; a person linked to every relative when a family hub already routes them). Do not add links the content does not support. Never add a 'See Also' section.");
        sb.AppendLine("5. ONE-WAY LINKS — links are directional. If A's note mentions B, only A links to B; B does not link back. The ONLY two-way relationship is hub ⇄ member (two purposeful edges). Do not add reciprocal backlinks.");
        sb.AppendLine("6. PREFERRED NAMES, NO DESCRIPTOR TITLES — a title is the everyday name (nickname, preferred name), NEVER a role, status, or formal name. '[REDACT] (Boyfriend)' is wrong: the role goes in the body, the title is '[REDACT]'. The formal/legal name goes inside under ## Info AND into the note's aliases. Rename via newName only when the preferred name is explicitly stated or clearly implied. Whenever a rename or merge changes a title, every alternate name (old title, formal name, nickname) must end up in 'aliases' so the note stays findable.");
        sb.AppendLine("7. ALIASES ARE LABELS, NOT NOTES — never create a separate note for a nickname or alternate name. Put every alternate name in the 'aliases' array of the canonical note's add/edit.");
        sb.AppendLine("8. DISAMBIGUATION — only when two DIFFERENT things share the EXACT same name, append a parenthetical to each ('Granny Squeak (person)' vs 'Granny Squeak (boat)'). Never use a parenthetical for a role or status, and never on a unique name.");
        sb.AppendLine("9. SUBFOLDER DEPTH CARRIES MEANING — a note at People/[REDACT]'s Family/Grandparents/Grumpy tells you who it is before it is opened. Do NOT flatten; move shallow notes deeper to reflect their place (People/Geoffrey → People/[REDACT]'s Family/Grandparents/Grumpy). Every path segment answers: what is this, whose is it, how does it relate?");
        sb.AppendLine("10. DATED EVENTS — every ## Events entry needs a specific or approximate date ('25th August 2024:', '~May 2026:', '2023:'). Never relative time ('recently', 'several years ago'). If no date can be found, move the fact into the body as prose.");
        sb.AppendLine("11. EVENT NOTES — notes in Events/ are point-in-time snapshots: dated, recording what happened, and linking OUTWARD to the ongoing note (Relationships/, People/) for the evolving story. Do not store evolving facts in an event note.");
        sb.AppendLine("12. NO DESCRIPTOR NOTES — descriptors/statuses ('Long Distance', 'Employed', 'Estranged') are fields or sentences inside the relevant note, never standalone notes. If you find one, move its information into the parent and add it to 'delete'.");
        sb.AppendLine("13. CHANGELOG — every note you edit must have a ## Changelog section with a dated entry for what changed. No [[links]] in changelog entries — plain text only.");
        sb.AppendLine("14. BROKEN LINKS — if a [[link]] target is not in the ALL EXISTING NOTE TITLES list: rename it to the correct title if obvious, otherwise delete it. If the broken link is the whole content of a bullet, delete the bullet.");
        sb.AppendLine("15. STAY IN YOUR FOLDER — only emit EDITS for notes whose path begins with the folder you are analysing. (Merges and adds may cross folders.) Cross-folder edits cause content loss when passes run in parallel.");
        sb.AppendLine("16. UNKNOWN FOLDER — notes in Unknown/ with a clear home move (via newName) to the right folder; if an Unknown/ note duplicates a real note, MERGE it; empty or descriptor-only stubs go in 'delete'.");
        sb.AppendLine();
        sb.AppendLine("PRESERVE CONTENT — do not invent or alter facts. Restructure links, titles, paths and hubs, and merge duplicates; keep the facts intact.");
        sb.AppendLine();
        sb.AppendLine("OUTPUT FORMAT — respond with ONLY raw JSON, starting immediately with {. No explanation, no preamble, no reasoning:");
        sb.AppendLine("{ \"add\": [{ \"name\": \"Folder/NoteName\", \"content\": \"full markdown\", \"aliases\": [\"AltName\"] }], \"edit\": [{ \"name\": \"EXACT current path from the --- header --- above\", \"newName\": \"NewFolder/NoteName\", \"content\": \"full markdown\", \"aliases\": [\"AltName\"] }], \"merge\": [{ \"from\": \"DuplicateTitle\", \"into\": \"CanonicalTitle\", \"reason\": \"same person\" }], \"delete\": [{ \"name\": \"NoteName\", \"reason\": \"brief reason\" }] }");
        sb.AppendLine("Rules: (a) An edit's 'name' MUST exactly match the path in the note's --- header --- (e.g. 'People/Family/Jake', not 'People/Jake'). (b) Omit newName if the note does not move. (c) Use 'merge' (bare titles) for two notes that are the same entity; use 'delete' ONLY for descriptor-only or empty notes — never delete a duplicate. (d) If nothing is needed: { \"add\": [], \"edit\": [], \"merge\": [], \"delete\": [] }");
        sb.AppendLine();
        sb.AppendLine("/no_think");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private record NoteData(string Title, string Folder, string Markdown, HashSet<string> Links);
    private record ClusterPlan(string Theme, string HubName, bool HubExists, List<string> Members);

    private static string BareTitle(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

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

    // ── JSON parsers ──────────────────────────────────────────────────────────────

    private static (List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes, List<EngramMerge> merges) ParseAddEdit(string raw)
    {
        raw = StripFences(raw);
        int start = raw.IndexOf('{');
        if (start < 0) return ([], [], [], []);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw[start..]);
            JsonElement root = doc.RootElement;

            List<EngramAdd> adds = [];
            if (root.TryGetProperty("add", out JsonElement addArr) && addArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in addArr.EnumerateArray())
                {
                    string? name    = el.GetStr("name");
                    string? content = el.GetStr("content");
                    if (!string.IsNullOrWhiteSpace(name) && content is not null)
                        adds.Add(new EngramAdd { NoteName = name, Content = content, Aliases = ParseAliasArray(el) });
                }

            List<EngramEdit> edits = [];
            if (root.TryGetProperty("edit", out JsonElement editArr) && editArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in editArr.EnumerateArray())
                {
                    string? name    = el.GetStr("name");
                    string? newName = el.GetStr("newName");
                    string? content = el.GetStr("content");
                    if (!string.IsNullOrWhiteSpace(name) && content is not null)
                        edits.Add(new EngramEdit { NoteName = name, NewNoteName = newName, Content = content, Aliases = ParseAliasArray(el) });
                }

            List<EngramDelete> deletes = [];
            if (root.TryGetProperty("delete", out JsonElement delArr) && delArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in delArr.EnumerateArray())
                {
                    string? name   = el.GetStr("name");
                    string? reason = el.GetStr("reason") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(name))
                        deletes.Add(new EngramDelete { NoteName = name, Reason = reason });
                }

            List<EngramMerge> merges = [];
            if (root.TryGetProperty("merge", out JsonElement mergeArr) && mergeArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in mergeArr.EnumerateArray())
                {
                    string? from   = el.GetStr("from");
                    string? into   = el.GetStr("into");
                    string? reason = el.GetStr("reason") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(into))
                        merges.Add(new EngramMerge { From = from, Into = into, Reason = reason });
                }

            return (adds, edits, deletes, merges);
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[Refactor] Failed to parse LLM output: {Error}. Raw (first 200): {Raw}",
                ex.Message, raw.Length > 200 ? raw[..200] : raw);
            return ([], [], [], []);
        }
    }

    private static List<ClusterPlan> ParseClusterPlan(string raw)
    {
        raw = StripFences(raw);
        int start = raw.IndexOf('{');
        if (start < 0) return [];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw[start..]);
            if (!doc.RootElement.TryGetProperty("clusters", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            List<ClusterPlan> clusters = new();
            foreach (JsonElement el in arr.EnumerateArray())
            {
                string? theme   = el.GetStr("theme");
                string? hubName = el.GetStr("hub_name");
                bool hubExists  = el.TryGetProperty("hub_exists", out JsonElement hx) && hx.ValueKind == JsonValueKind.True;
                List<string> members = el.TryGetProperty("members", out JsonElement ma) && ma.ValueKind == JsonValueKind.Array
                    ? ma.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                    : [];

                if (!string.IsNullOrWhiteSpace(theme) && !string.IsNullOrWhiteSpace(hubName))
                    clusters.Add(new ClusterPlan(theme, hubName, hubExists, members));
            }
            return clusters;
        }
        catch { return []; }
    }

    private static IReadOnlyList<string> ParseAliasArray(JsonElement el)
    {
        if (!el.TryGetProperty("aliases", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static string StripFences(string raw)
        => Regex.Replace(raw, @"```[a-zA-Z]*\n?", string.Empty).Trim('`').Trim();
}

file static class RefactorJson
{
    internal static string? GetStr(this JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

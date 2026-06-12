using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Refactor : Agent
{
    private readonly BrainService brain;
    private readonly Engram?      engram;
    private readonly SemaphoreSlim runLock = new(1, 1);

    private const int SINGLE_PASS_THRESHOLD = 15;
    private const int CLUSTER_CALL_LIMIT    = 20;
    private const int EXCERPT_LENGTH        = 300;

    internal override ThreadType Type => ThreadType.Refactor;

    internal Refactor(AgentConfig config, BrainService brain, Engram? engram = null) : base(config)
    {
        this.brain  = brain;
        this.engram = engram;
    }

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
            Common.Logger.LogInformation("[Refactor] Engram paused for refactor.");
        }

        try
        {
            Common.Logger.LogInformation("[Refactor] Starting {Mode} pass.", allNotes ? "full" : "incremental");

            // ── Backup ────────────────────────────────────────────────────────────
            string backupResult = await brain.Backup();
            Common.Logger.LogInformation("[Refactor] {Backup}", backupResult);

            // ── Clean duplicate Unknown stubs ─────────────────────────────────────
            int stubsDeleted = await brain.CleanUnknownStubs();
            if (stubsDeleted > 0)
                Common.Logger.LogInformation("[Refactor] Deleted {Count} duplicate Unknown stub(s).", stubsDeleted);

            // ── Seed ──────────────────────────────────────────────────────────────
            List<string> seedTitles = allNotes
                ? await brain.GetNoteTitles()
                : await brain.GetDirtyNotes();

            if (seedTitles.Count == 0)
                return allNotes
                    ? "Brain is empty — nothing to refactor."
                    : "No dirty notes — graph is up to date. Use `/refactor all` for a full scan.";

            Common.Logger.LogInformation("[Refactor] Seed: {Count} note(s).", seedTitles.Count);

            // ── Load seed notes ───────────────────────────────────────────────────
            Dictionary<string, NoteData> loaded = new(StringComparer.OrdinalIgnoreCase);

            async Task Load(string title)
            {
                if (loaded.ContainsKey(title)) return;
                string? raw = await brain.GetNote(title);
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
                foreach (string t in brain.GetTitlesByFolder(folder))
                    await Load(t);
            }

            // Load root-level hub notes regardless (LLM needs to know what hubs exist)
            foreach (string t in brain.GetTitlesByFolder(string.Empty))
                await Load(t);

            Common.Logger.LogInformation("[Refactor] Working set: {Count} note(s) across folder(s): {Folders}.",
                loaded.Count, string.Join(", ", touchedFolders));

            // ── All titles (lightweight, from cache) ──────────────────────────────
            List<string> allTitles = await brain.GetNoteTitles();

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
                Common.Logger.LogInformation("[Refactor] Stripped See Also / orphan bullets from '{Title}'.", title);
            }

            if (seeAlsoEdits.Count > 0)
                Common.Logger.LogInformation("[Refactor] Pre-processed {Count} note(s) (See Also strip + orphan bullet removal).", seeAlsoEdits.Count);

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

            foreach ((string folder, List<NoteData> notes) in byFolder)
            {
                Common.Logger.LogInformation("[Refactor] Analysing folder '{Folder}' ({Count} note(s)).", folder, notes.Count);

                List<(List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes)> results;

                if (notes.Count <= SINGLE_PASS_THRESHOLD)
                {
                    // Small folder — one call handles everything
                    (List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes) = await AnalyseFolder(folder, notes, hubNotes, allTitles);
                    results = [(adds, edits, deletes)];
                }
                else
                {
                    // Large folder — detect clusters first, then analyse each cluster
                    results = await AnalyseLarge(folder, notes, hubNotes, allTitles);
                }

                foreach ((List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes) in results)
                {
                    allAdds.AddRange(adds);
                    allEdits.AddRange(edits);
                    allDeletes.AddRange(deletes);
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

            // ── Merge and apply operations ────────────────────────────────────────
            // LLM edits take priority: they already saw stripped content, so their output
            // is a superset of the See Also removal.
            Dictionary<string, EngramEdit> mergedEdits = new(seeAlsoEdits, StringComparer.OrdinalIgnoreCase);
            foreach (EngramEdit edit in allEdits)
                mergedEdits[edit.NoteName] = edit;

            List<EngramEdit> finalEdits = mergedEdits.Values.ToList();

            if (allAdds.Count > 0)
            {
                Common.Logger.LogInformation("[Refactor] Applying {Count} add(s).", allAdds.Count);
                await brain.AddNotes(allAdds);
            }

            if (finalEdits.Count > 0)
            {
                Common.Logger.LogInformation("[Refactor] Applying {Count} edit(s) ({SeeAlso} See Also strip(s), {Llm} LLM edit(s)).",
                    finalEdits.Count, seeAlsoEdits.Count, allEdits.Count);
                await brain.EditNotes(finalEdits);
            }

            if (allDeletes.Count > 0)
            {
                Common.Logger.LogInformation("[Refactor] Applying {Count} delete(s).", allDeletes.Count);
                foreach (EngramDelete del in allDeletes)
                {
                    Common.Logger.LogInformation("[Refactor] Deleting '{Name}': {Reason}", del.NoteName, del.Reason);
                    await brain.DeleteNote(del.NoteName);
                }
            }

            // ── Clear dirty flags ─────────────────────────────────────────────────
            await brain.ClearDirty(loaded.Keys);

            StringBuilder summary = new();
            summary.AppendLine($"Refactor {(allNotes ? "full" : "incremental")} complete — {allAdds.Count} added, {finalEdits.Count} edited, {allDeletes.Count} deleted.");
            if (!allNotes) summary.AppendLine($"Working set: {seedTitles.Count} dirty note(s) expanded to {loaded.Count} across [{string.Join(", ", touchedFolders)}].");
            if (seeAlsoEdits.Count > 0) summary.AppendLine($"See Also sections removed: {seeAlsoEdits.Count}.");
            return summary.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            Common.Logger.LogError("[Refactor] Failed: {Message}", ex.Message);
            return $"Refactor failed: {ex.Message}";
        }
        finally
        {
            if (engramWasEnabled)
            {
                engram!.Enable();
                Common.Logger.LogInformation("[Refactor] Engram restored.");
            }
            runLock.Release();
        }
    }

    // ── Folder analysis ───────────────────────────────────────────────────────────

    /// <summary>
    /// Single-call analysis for folders with ≤ SinglePassThreshold notes.
    /// The LLM receives all note content and existing hubs, and outputs adds + edits + deletes.
    /// </summary>
    private async Task<(List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes)> AnalyseFolder(
        string folder,
        List<NoteData> notes,
        List<NoteData> hubNotes,
        List<string> allTitles)
    {
        string threadKey = $"refactor-folder-{folder}:{Guid.NewGuid()}";
        string prompt    = BuildFolderPrompt(folder, notes, hubNotes, allTitles);

        Common.Logger.LogInformation("[Refactor] Folder '{Folder}': single-pass LLM call ({Count} notes).", folder, notes.Count);
        string raw = await Prompt(threadKey, prompt, maxTokensOverride: -1);
        (List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes) = ParseAddEdit(raw);

        // Enforce folder scope: only accept edits for notes that live inside this folder.
        // This prevents cross-folder edits (e.g. Relationships pass rewriting Events/ notes).
        edits = edits.Where(e =>
        {
            string path = e.NoteName;
            string editFolder = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
            return string.IsNullOrEmpty(folder)
                || string.Equals(editFolder, folder, StringComparison.OrdinalIgnoreCase)
                || editFolder.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }).ToList();

        return (adds, edits, deletes);
    }

    /// <summary>
    /// Two-pass analysis for folders exceeding the single-pass threshold.
    /// Pass 1: summary view → cluster plan.
    /// Pass 2: one analysis call per cluster with full note content.
    /// </summary>
    private async Task<List<(List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes)>> AnalyseLarge(
        string folder,
        List<NoteData> notes,
        List<NoteData> hubNotes,
        List<string> allTitles)
    {
        // Pass 1 — Cluster detection (titles + short excerpt per note)
        string p1Key    = $"refactor-clusters-{folder}:{Guid.NewGuid()}";
        string p1Prompt = ClusterDetectionPrompt(folder, notes, hubNotes);

        Common.Logger.LogInformation("[Refactor] Folder '{Folder}': cluster detection pass ({Count} notes).", folder, notes.Count);
        string clusterRaw     = await Prompt(p1Key, p1Prompt, maxTokensOverride: -1);
        List<ClusterPlan> clusters = ParseClusterPlan(clusterRaw);

        if (clusters.Count == 0)
        {
            Common.Logger.LogInformation("[Refactor] Folder '{Folder}': no clusters identified — skipping.", folder);
            return [];
        }

        Common.Logger.LogInformation("[Refactor] Folder '{Folder}': {Count} cluster(s) identified.", folder, clusters.Count);

        // Pass 2 — One analysis call per cluster
        List<(List<EngramAdd>, List<EngramEdit>, List<EngramDelete>)> results = new();
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

            string p2Key    = $"refactor-cluster-{cluster.Theme}:{Guid.NewGuid()}";
            string p2Prompt = ClusterAnalysisPrompt(cluster, clusterNotes, existingHub, hubNotes, allTitles);

            Common.Logger.LogInformation("[Refactor] Cluster '{Theme}' ({Count} notes).", cluster.Theme, clusterNotes.Count);
            string raw = await Prompt(p2Key, p2Prompt, maxTokensOverride: -1);
            (List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes) = ParseAddEdit(raw);

            // Enforce cluster scope: only accept edits for notes that are members of this cluster.
            // Adds (new hub notes) are always allowed. This prevents later cluster passes from
            // overwriting content written by earlier passes for notes outside their scope.
            HashSet<string> memberTitles = clusterNotes.Select(n => n.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
            edits = edits.Where(e => memberTitles.Contains(BareTitle(e.NoteName))).ToList();

            results.Add((adds, edits, deletes));
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
        sb.AppendLine("YOUR TASKS:");
        sb.AppendLine("1. CLUSTER — identify thematic groups (family, friends, colleagues, hardware, etc.). A note can belong to multiple clusters.");
        sb.AppendLine("2. HUBS — for each meaningful cluster, determine if a hub note should exist. Hubs reduce clutter: individual members link to their hub, and the hub links to each member. Update existing hubs or create new ones.");
        sb.AppendLine("3. LINK ROUTING — update note content to route links through appropriate hubs where it reduces clutter. Only change links that genuinely belong in the hub relationship.");
        sb.AppendLine("4. BROKEN LINKS — if a [[link]] target is not in the ALL EXISTING NOTE TITLES list: rename it to the correct title if an obvious match exists, or delete it. If the broken link is the sole content of a bullet point (e.g. '- [[Missing]]'), delete the entire bullet line.");
        sb.AppendLine("5. PRESERVE CONTENT — do not alter the factual content of any note. Only restructure links and add/update hub notes.");
        sb.AppendLine("6. DO NOT add links that are not supported by the note's content. DO NOT add See Also sections.");
        sb.AppendLine("7. ONE-WAY LINKS — links are directional. Do NOT add return links from spoke notes back to the root person. Spokes point outward, not back.");
        sb.AppendLine("8. USE SUBFOLDERS — subfolder depth carries meaning. A note at People/[REDACT]'s Family/Grandparents/Grumpy tells you who this is before the note is opened. Do NOT flatten notes to a shallower path. If a note is currently too shallow, move it deeper to reflect its place in the graph (e.g. People/Geoffrey → People/[REDACT]'s Family/Grandparents/Grumpy). Every path segment should answer a question: what is this, whose is it, how does it relate?");
        sb.AppendLine("9. PREFERRED NAMES — a note's title must be the everyday name (nickname, alias, preferred name), not the formal or legal name. The formal name belongs inside the note under ## Info (e.g. **Full Name:** Geoffrey). Use newName to rename. This is essential for both recall accuracy and duplicate prevention: two notes with different formal and informal names for the same person must be merged under the preferred name. Only rename when the preferred name is explicitly stated or clearly implied in the note's own content — do not guess. Example: a note titled 'Geoffrey' whose content states 'Nickname: Grumpy' MUST be renamed to 'Grumpy' via newName.");
        sb.AppendLine("9a. DISAMBIGUATION — when two DIFFERENT things share the EXACT same name, append a parenthetical to each to make them unambiguous, Wikipedia-style (e.g. 'Granny Squeak (person)' vs 'Granny Squeak (boat)'). Only apply this when an actual collision exists or would be created by a rename — do NOT add parentheticals to unique names that have no collision.");
        sb.AppendLine("10. DATED EVENTS — every entry in an ## Events section must carry a specific or approximate date. Remove or rewrite any undated event entries. Use the format '25th August 2024: ...' for known dates, '~May 2026: ...' for approximate, '2023: ...' for year-only. Never use relative time ('several years ago', 'recently') — these rot as time passes. If a date cannot be determined, move the fact into the note body as prose rather than listing it under Events.");
        sb.AppendLine("11. CHANGELOG — every note you edit must have a ## Changelog section. Add a dated entry for what changed (e.g. '- 2026-06-02: Added employment info.'). Do NOT include [[links]] in changelog entries — plain text only.");
        sb.AppendLine("12. EVENT NOTES — notes inside Events/ are point-in-time snapshots. They must carry a specific or approximate date. They record what happened at that moment and link outward to ongoing notes (Relationships/, People/, etc.) for the evolving story. Do not store ongoing or general facts in an event note.");
        sb.AppendLine("13. NO DESCRIPTOR NOTES — descriptors, statuses, and labels are not notes. 'Long Distance Relationship', 'Employed', 'Estranged' belong as a field or sentence inside the relevant note (e.g. 'Current Status: Long distance' in the relationship note). If you encounter a note that is purely a descriptor with no content of its own, its information should be moved into the parent note and the descriptor note should be added to the 'delete' list.");
        sb.AppendLine("14. STAY IN YOUR FOLDER — only emit edits for notes whose path begins with the folder you are analysing. Do NOT rewrite notes in other folders, even if they are referenced here. Cross-folder edits cause content loss when multiple passes run.");
        sb.AppendLine("15. UNKNOWN FOLDER — notes in Unknown/ that have a clear home should be moved (via newName) to the correct folder. Specifically: ARI agent notes (Recall, Dialogue, Engram) belong under Projects/ARI/Agents/; place names ([REDACT], etc.) belong under Places/; empty or descriptor-only stubs should be deleted.");
        sb.AppendLine();
        sb.AppendLine("OUTPUT FORMAT — respond with ONLY raw JSON, starting immediately with {. No explanation, no preamble, no reasoning:");
        sb.AppendLine("{ \"add\": [{ \"name\": \"Folder/NoteName\", \"content\": \"full markdown\" }], \"edit\": [{ \"name\": \"EXACT current path from the --- header --- above\", \"newName\": \"NewFolder/NoteName\", \"content\": \"full markdown\" }], \"delete\": [{ \"name\": \"NoteName\", \"reason\": \"brief reason\" }] }");
        sb.AppendLine("Rules: (a) The 'name' field in an edit MUST exactly match the path shown in the note's --- header --- (e.g. 'People/Family/Jake', not 'People/Jake'). (b) Omit newName if the note does not move. (c) Use 'delete' for descriptor-only or empty notes that should be removed entirely — do NOT put them in 'edit' with empty content. (d) If no changes are needed: { \"add\": [], \"edit\": [], \"delete\": [] }");
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

    private static (List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes) ParseAddEdit(string raw)
    {
        raw = StripFences(raw);
        int start = raw.IndexOf('{');
        if (start < 0) return ([], [], []);

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
                        adds.Add(new EngramAdd { NoteName = name, Content = content });
                }

            List<EngramEdit> edits = [];
            if (root.TryGetProperty("edit", out JsonElement editArr) && editArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in editArr.EnumerateArray())
                {
                    string? name    = el.GetStr("name");
                    string? newName = el.GetStr("newName");
                    string? content = el.GetStr("content");
                    if (!string.IsNullOrWhiteSpace(name) && content is not null)
                        edits.Add(new EngramEdit { NoteName = name, NewNoteName = newName, Content = content });
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

            return (adds, edits, deletes);
        }
        catch (Exception ex)
        {
            Common.Logger.LogWarning("[Refactor] Failed to parse LLM output: {Error}. Raw (first 200): {Raw}",
                ex.Message, raw.Length > 200 ? raw[..200] : raw);
            return ([], [], []);
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

    private static string StripFences(string raw)
        => Regex.Replace(raw, @"```[a-zA-Z]*\n?", string.Empty).Trim('`').Trim();
}

file static class RefactorJson
{
    internal static string? GetStr(this JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

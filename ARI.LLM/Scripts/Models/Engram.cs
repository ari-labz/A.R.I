using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Engram : Model, IDisposable
{
    private readonly Dialogue dialogue;
    private readonly BrainService brain;
    private readonly Context? context;

    private readonly Dictionary<string, DateTime> lastEngramRun = new();
    private readonly Dictionary<string, int> lastEngramHistoryCount = new();
    private readonly SemaphoreSlim engramLock = new(1, 1);
    private readonly Timer? sweepTimer;
    private readonly int fetchDepth;
    private TimeSpan sweepInterval;
    private readonly HttpClient httpClient = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    internal Engram(ModelConfig config, Dialogue dialogue, BrainService brain, Context? context, int sweepIntervalMinutes, int fetchDepth = 7) : base(config)
    {
        this.dialogue   = dialogue;
        this.brain      = brain;
        this.context    = context;
        this.fetchDepth = fetchDepth;

        // Ignore the history snapshot passed by the event — it is pre-trim and may be larger
        // than the Dialogue short-term memory window. Re-fetch the already-trimmed buffer when
        // the task actually runs so Engram always sees the same capped view as Dialogue.
        dialogue.ThreadBufferFull += (threadKey, ignored) =>
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => RunEngramAsync(threadKey, "chat buffer"));

        if (sweepIntervalMinutes > 0)
        {
            sweepInterval = TimeSpan.FromMinutes(sweepIntervalMinutes);
            sweepTimer    = new Timer(_ => _ = SweepThreadsAsync(), null, sweepInterval, Timeout.InfiniteTimeSpan);
            Common.Logger.LogInformation("Engram sweep timer active: every {N} minutes.", sweepIntervalMinutes);
        }
    }

    internal bool IsEnabled { get; private set; } = true;

    internal void Enable()
    {
        IsEnabled = true;
        Common.Logger.LogInformation("[Engram] Enabled.");
    }

    internal void Disable()
    {
        IsEnabled = false;
        Common.Logger.LogInformation("[Engram] Disabled.");
    }

    /// <summary>
    /// Manually triggers a sweep of all active threads, regardless of the sweep timer.
    /// Respects the enabled flag — if Engram is disabled, the sweep is a no-op.
    /// </summary>
    internal async Task ManualSweepAsync()
    {
        if (!IsEnabled)
        {
            Common.Logger.LogInformation("[Engram] Manual sweep requested but Engram is disabled.");
            return;
        }
        Common.Logger.LogInformation("[Engram] Manual sweep triggered.");
        await SweepThreadsAsync(resetTimer: false);
    }

    internal Task<int> PurgeNotes() => brain.PurgeAllNotes();

    /// <summary>
    /// Loads the full graph and asks the Refactor model to restructure it.
    /// Each phase runs in its own fresh thread so the accumulated context never
    /// exceeds the model's context window.  Edit operations are chunked by note
    /// group so no single response needs to produce a huge JSON blob.
    /// </summary>
    internal async Task<string> RefactorAsync()
    {
        if (!await engramLock.WaitAsync(TimeSpan.FromSeconds(5)))
            return "Refactor skipped — Engram is busy. Try again in a moment.";

        try
        {
            Common.Logger.LogInformation("[Refactor] Starting graph refactor pass.");

            // ── Load full graph ──────────────────────────────────────────────────
            List<string> allTitles = await brain.GetNoteTitles();
            if (allTitles.Count == 0)
                return "Brain is empty — nothing to refactor.";

            StringBuilder graphBlock = new();
            foreach (string title in allTitles)
            {
                string? content = await brain.GetNote(title);
                if (content is null) continue;
                graphBlock.AppendLine($"--- {title} ---");
                graphBlock.AppendLine(content);
                graphBlock.AppendLine("---");
            }
            string graph = graphBlock.ToString();

            // ── Shared instructions injected into every fresh thread ─────────────
            string architecture =
                "You are performing a structural refactor of ARI's memory graph.\n\n" +
                "Your job is ARCHITECTURE ONLY. Do not change any facts. Do not add new information. " +
                "Preserve all existing content verbatim — only reorganise structure, folders, links, and hierarchy.\n\n" +
                "## HOW NOTE PATHS WORK\n" +
                "Notes are stored at paths like `Category/NoteName`.\n" +
                "The path in the `name` field controls where in the tree the note lives:\n" +
                "- `People/[REDACT]`  → note named '[REDACT]' inside the 'People' folder\n" +
                "- `People`       → the 'People' folder note itself (ROOT level, NO prefix, NO slash)\n" +
                "- `Unknown/[REDACT]` → note in the 'Unknown' folder (wrong for anything categorisable)\n" +
                "ROOT-LEVEL HUB NOTES use a SINGLE bare name with NO slash: `People`, `Places`, `Hardware`.\n" +
                "To MOVE a note: use edit with newName set to the correct path:\n" +
                "  { \"name\": \"Unknown/Foo\", \"newName\": \"People/Foo\", \"content\": \"...\" }\n\n" +
                "## CORE RULES\n" +
                "- Unknown/ must be EMPTY after a refactor — every note there must be moved.\n" +
                "- NEVER delete a category folder note (People, Places, Unknown, Hardware, etc.) — cascades in Trilium.\n" +
                "- [[links]] inside content use bare note names only, never folder paths.\n" +
                "- Family hubs are named after their owner: '[REDACT]'s Family', not 'Family'.\n\n";

            // ── Phase 1 — Audit (fresh thread, discarded after) ──────────────────
            string p1Key = $"refactor-p1:{Guid.NewGuid()}";
            string auditPrompt =
                architecture +
                "## AUDIT STEPS — work through ALL of these in order\n" +
                "1. List every note in `Unknown/`. For EACH one decide its correct category and mark for move.\n" +
                "2. List every category folder that exists or should exist.\n" +
                "3. For each category, verify a root-level hub note exists. If missing, mark for creation.\n" +
                "4. For each hub, check child list is complete and has descriptive text. Mark for update if not.\n" +
                "5. Check for duplicate notes covering the same entity — propose merges.\n" +
                "6. Check for empty stubs — propose deletions.\n" +
                "7. For every note, check lateral links to domain siblings. Mark missing ones.\n" +
                "8. HUB ABSORPTION — check personal notes (especially [REDACT]) for flat link lists that belong on a hub.\n" +
                "9. FAMILY NOTE NAMING — rename any 'Family' note to '[REDACT]'s Family'; update every [[Family]] link.\n\n" +
                $"FULL GRAPH:\n{graph}\n\n" +
                "Output your plan as a numbered list. Work through ALL audit steps. " +
                "Step 1 (emptying Unknown/) is highest priority — list every Unknown note and its destination.\n" +
                "At the end, output a SUMMARY SECTION headed '## Note Groups' that lists the notes to edit, " +
                "split into these groups:\n" +
                "- GROUP_UNKNOWN: notes currently in Unknown/ (to be moved)\n" +
                "- GROUP_HUBS: root-level hub notes to create or update (People, Places, Software, etc.)\n" +
                "- GROUP_PEOPLE: individual People/* notes to update\n" +
                "- GROUP_OTHER: everything else (Places/*, Hardware/*, Projects/*, etc.)";

            string plan = await PromptThread(p1Key, auditPrompt, maxTokensOverride: -1);
            Common.Logger.LogInformation("[Refactor] Audit plan:\n{Plan}", plan);

            // ── Phase 2a — ADD (fresh thread) ────────────────────────────────────
            // New hub notes or structural notes that do not yet exist.
            string p2aKey = $"refactor-p2a:{Guid.NewGuid()}";
            string addPrompt =
                architecture +
                $"REFACTOR PLAN (for context):\n{plan}\n\n" +
                "Output ONLY the ADD operations — notes that do NOT yet exist and must be created.\n" +
                "FORMAT (raw JSON array, no fences, no explanation):\n" +
                "[ { \"name\": \"Category/NoteName\", \"content\": \"markdown\" }, ... ]\n" +
                "If nothing to add: []";

            string addRaw = await PromptThread(p2aKey, addPrompt, maxTokensOverride: -1);
            Common.Logger.LogInformation("[Refactor] Phase 2a (adds) raw:\n{Raw}", addRaw);
            List<EngramAdd> adds = ParseAddArray(addRaw);
            if (adds.Count > 0)
            {
                Common.Logger.LogInformation("[Refactor] Applying {Count} add(s).", adds.Count);
                await brain.AddNotes(adds);
            }

            // ── Phase 2b — EDIT in four focused groups (one fresh thread each) ───
            // Splitting by group keeps each prompt+response well under the context limit.
            var editGroups = new[]
            {
                (
                    label: "Unknown migrations",
                    key:   $"refactor-p2b-unknown:{Guid.NewGuid()}",
                    scope: "ONLY notes currently in `Unknown/` (GROUP_UNKNOWN from the plan). " +
                           "Every Unknown/* note MUST appear here with a correct newName."
                ),
                (
                    label: "Hub notes",
                    key:   $"refactor-p2b-hubs:{Guid.NewGuid()}",
                    scope: "ONLY root-level hub notes (GROUP_HUBS from the plan). " +
                           "These are single-segment names like `People`, `Places`, `Software`. " +
                           "Ensure each has a descriptive paragraph and a complete child directory."
                ),
                (
                    label: "People/* notes",
                    key:   $"refactor-p2b-people:{Guid.NewGuid()}",
                    scope: "ONLY individual `People/*` notes (GROUP_PEOPLE from the plan). " +
                           "Apply hub absorption (replace flat family/friend lists with hub links), " +
                           "add lateral links, and rename Family→[REDACT]'s Family references."
                ),
                (
                    label: "Other category notes",
                    key:   $"refactor-p2b-other:{Guid.NewGuid()}",
                    scope: "ONLY notes in `Places/*`, `Hardware/*`, `Projects/*`, `Relationships/*`, " +
                           "`Events/*`, `Organisations/*`, and `Software/*` (GROUP_OTHER from the plan). " +
                           "Add lateral links and correct any structural issues."
                ),
            };

            List<EngramEdit> allEdits = new();
            foreach (var (label, key, scope) in editGroups)
            {
                string groupPrompt =
                    architecture +
                    $"REFACTOR PLAN (for context):\n{plan}\n\n" +
                    $"FULL GRAPH (current state):\n{graph}\n\n" +
                    $"Output ONLY the EDIT operations for: {scope}\n\n" +
                    "FORMAT (raw JSON array, no fences, no explanation):\n" +
                    "[ { \"name\": \"CurrentPath/NoteName\", \"newName\": \"NewPath/NoteName\", \"content\": \"full markdown\" }, ... ]\n" +
                    "Omit `newName` when the note does not move.\n" +
                    "If no edits for this group: []";

                string groupRaw = await PromptThread(key, groupPrompt, maxTokensOverride: -1);
                Common.Logger.LogInformation("[Refactor] Phase 2b ({Label}) raw (first 300 chars): {Raw}",
                    label, groupRaw.Length > 300 ? groupRaw[..300] + "..." : groupRaw);

                List<EngramEdit> groupEdits = ParseEditArray(groupRaw);
                if (groupEdits.Count > 0)
                {
                    Common.Logger.LogInformation("[Refactor] Applying {Count} edit(s) for group '{Label}'.", groupEdits.Count, label);
                    await brain.EditNotes(groupEdits);
                    allEdits.AddRange(groupEdits);
                }
                else
                {
                    Common.Logger.LogInformation("[Refactor] No edits for group '{Label}'.", label);
                }
            }

            // ── Phase 2c — DELETE (fresh thread, plan only — no graph needed) ────
            string p2cKey = $"refactor-p2c:{Guid.NewGuid()}";
            string deletePrompt =
                architecture +
                $"REFACTOR PLAN (for context):\n{plan}\n\n" +
                "Output ONLY the DELETE operations — stub notes to remove or notes merged into another.\n" +
                "FORMAT (raw JSON array, no fences, no explanation):\n" +
                "[ { \"name\": \"NoteName\", \"reason\": \"why\" }, ... ]\n" +
                "NEVER include category folder notes (People, Places, Unknown, Hardware, etc.).\n" +
                "If nothing to delete: []";

            string deleteRaw = await PromptThread(p2cKey, deletePrompt, maxTokensOverride: -1);
            List<EngramDelete> deletes = ParseDeleteArray(deleteRaw);

            int total = adds.Count + allEdits.Count + deletes.Count;
            if (total == 0)
            {
                Common.Logger.LogInformation("[Refactor] Model returned empty operation lists — no changes needed.");
                return "Refactor complete — model found no structural changes needed.";
            }

            Common.Logger.LogInformation("[Refactor] Applying {Count} delete(s).", deletes.Count);
            foreach (EngramDelete del in deletes)
            {
                Common.Logger.LogInformation("[Refactor] Deleting '{Name}': {Reason}", del.NoteName, del.Reason);
                await brain.DeleteNote(del.NoteName);
            }

            // ── Phase 3 — Link density audit (fresh thread, re-reads updated graph) ─
            Common.Logger.LogInformation("[Refactor] Structure applied. Starting Phase 3 — link density audit.");

            // Reload graph so the model sees the post-refactor state.
            StringBuilder updatedGraphBlock = new();
            foreach (string title in await brain.GetNoteTitles())
            {
                string? content = await brain.GetNote(title);
                if (content is null) continue;
                updatedGraphBlock.AppendLine($"--- {title} ---");
                updatedGraphBlock.AppendLine(content);
                updatedGraphBlock.AppendLine("---");
            }

            string p3Key = $"refactor-p3:{Guid.NewGuid()}";
            string linkAuditPrompt =
                architecture +
                $"UPDATED GRAPH (post-refactor):\n{updatedGraphBlock}\n\n" +
                "The structural refactor is complete. Now perform a link density audit.\n\n" +
                "For EVERY note, check:\n" +
                "- Person ↔ Places they live, work, or study\n" +
                "- Person ↔ Events they attended\n" +
                "- Person ↔ People they know directly\n" +
                "- Person ↔ Hardware they own or Organisations they belong to\n" +
                "- Place ↔ Organisations / Events there\n" +
                "- Thematic hub ↔ every note it covers\n\n" +
                "Output ONLY an edit array — preserve all existing content and append missing links to a ## See Also section.\n" +
                "FORMAT (raw JSON array, no fences, no explanation):\n" +
                "[ { \"name\": \"CurrentPath/NoteName\", \"content\": \"full markdown\" }, ... ]\n" +
                "If link density is already sufficient: []";

            string linkRaw = await PromptThread(p3Key, linkAuditPrompt, maxTokensOverride: -1);
            List<EngramEdit> linkEdits = ParseEditArray(linkRaw);

            if (linkEdits.Count > 0)
            {
                Common.Logger.LogInformation("[Refactor] Phase 3: applying {Count} link edit(s).", linkEdits.Count);
                await brain.EditNotes(linkEdits);
                await ApplyBacklinksAsync(new List<EngramAdd>(), linkEdits);
            }
            else
            {
                Common.Logger.LogInformation("[Refactor] Phase 3: no additional links needed.");
            }

            return $"Refactor complete — {adds.Count} added, {allEdits.Count} edited, {deletes.Count} deleted" +
                   (linkEdits.Count > 0 ? $", {linkEdits.Count} link edit(s) in Phase 3." : ".");
        }
        catch (Exception ex)
        {
            Common.Logger.LogError("[Refactor] Failed: {Message}", ex.Message);
            return $"Refactor failed: {ex.Message}";
        }
        finally
        {
            engramLock.Release();
        }
    }

    private static List<EngramAdd> ParseAddArray(string raw)
    {
        raw = StripFences(raw);
        int start = raw.IndexOf('[');
        if (start < 0) return new();
        raw = raw[start..];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            List<EngramAdd> result = new();
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                string? name    = el.GetString("name");
                string? content = el.GetString("content");
                if (!string.IsNullOrWhiteSpace(name) && content is not null)
                    result.Add(new EngramAdd { NoteName = name, Content = content });
            }
            return result;
        }
        catch { return new(); }
    }

    private static List<EngramEdit> ParseEditArray(string raw)
    {
        raw = StripFences(raw);
        int start = raw.IndexOf('[');
        if (start < 0) return new();
        raw = raw[start..];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            List<EngramEdit> result = new();
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                string? name    = el.GetString("name");
                string? newName = el.GetString("newName");
                string? content = el.GetString("content");
                if (!string.IsNullOrWhiteSpace(name) && content is not null)
                    result.Add(new EngramEdit { NoteName = name, NewNoteName = newName, Content = content });
            }
            return result;
        }
        catch { return new(); }
    }

    private static List<EngramDelete> ParseDeleteArray(string raw)
    {
        raw = StripFences(raw);
        int start = raw.IndexOf('[');
        if (start < 0) return new();
        raw = raw[start..];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            List<EngramDelete> result = new();
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                string? name   = el.GetString("name");
                string? reason = el.GetString("reason");
                if (!string.IsNullOrWhiteSpace(name))
                    result.Add(new EngramDelete { NoteName = name, Reason = reason ?? string.Empty });
            }
            return result;
        }
        catch { return new(); }
    }

    private static string StripFences(string raw)
        => System.Text.RegularExpressions.Regex.Replace(raw, @"```[a-zA-Z]*\n?", "").Trim('`').Trim();

    private static (List<EngramAdd> adds, List<EngramEdit> edits, List<EngramDelete> deletes, bool parseOk) ParseRefactorOutput(string raw)
    {
        raw = StripFences(raw);

        // Find the outermost JSON object — ignore any preamble text.
        int start = raw.IndexOf('{');
        if (start >= 0) raw = raw[start..];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;

            List<EngramAdd> adds = new();
            if (root.TryGetProperty("add", out JsonElement addArr) && addArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in addArr.EnumerateArray())
                {
                    string? name    = el.GetString("name");
                    string? content = el.GetString("content");
                    if (!string.IsNullOrWhiteSpace(name) && content is not null)
                        adds.Add(new EngramAdd { NoteName = name, Content = content });
                }

            List<EngramEdit> edits = new();
            if (root.TryGetProperty("edit", out JsonElement editArr) && editArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in editArr.EnumerateArray())
                {
                    string? name    = el.GetString("name");
                    string? newName = el.GetString("newName");
                    string? content = el.GetString("content");
                    if (!string.IsNullOrWhiteSpace(name) && content is not null)
                        edits.Add(new EngramEdit { NoteName = name, NewNoteName = newName, Content = content });
                }

            List<EngramDelete> deletes = new();
            if (root.TryGetProperty("delete", out JsonElement delArr) && delArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in delArr.EnumerateArray())
                {
                    string? name   = el.GetString("name");
                    string? reason = el.GetString("reason");
                    if (!string.IsNullOrWhiteSpace(name))
                        deletes.Add(new EngramDelete { NoteName = name, Reason = reason ?? string.Empty });
                }

            return (adds, edits, deletes, parseOk: true);
        }
        catch
        {
            return (new(), new(), new(), parseOk: false);
        }
    }

    public void Dispose()
    {
        sweepTimer?.Dispose();
        engramLock.Dispose();
        httpClient.Dispose();
    }

    // ── Private ──────────────────────────────────────────────────────────────────

    private async Task SweepThreadsAsync(bool resetTimer = true)
    {
        try
        {
            foreach (string threadKey in dialogue.ThreadKeys)
            {
                DateTime lastRun     = lastEngramRun.TryGetValue(threadKey, out DateTime t) ? t : DateTime.MinValue;
                DateTime lastMessage = dialogue.GetThreadLastMessageAt(threadKey);
                if (lastMessage <= lastRun) continue;

                IReadOnlyList<ChatMessage> history = dialogue.GetThreadHistory(threadKey);
                if (history.Count > 1)
                    await RunEngramAsync(threadKey, "sweep timer");
            }
        }
        finally
        {
            if (resetTimer)
                sweepTimer?.Change(sweepInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RunEngramAsync(string threadKey, string trigger)
    {
        if (!IsEnabled) return;
        if (!await engramLock.WaitAsync(0)) return;
        try
        {
            // Always fetch the already-trimmed, token-capped buffer from Dialogue so Engram
            // sees the same capped view of the conversation that ARI herself is working with.
            IReadOnlyList<ChatMessage> history = dialogue.GetThreadHistory(threadKey);

            // Slice to messages since the last Engram run on this thread.
            int lastCount = lastEngramHistoryCount.TryGetValue(threadKey, out int c) ? c : 0;
            IReadOnlyList<ChatMessage> recentMessages = history.Skip(lastCount).ToList();

            lastEngramRun[threadKey]          = DateTime.UtcNow;
            lastEngramHistoryCount[threadKey] = history.Count;

            Common.Logger.LogInformation("[Engram] [{ThreadKey}] sweep triggered (trigger: {Trigger})", threadKey, trigger);

            // --- Phase 1: Classify ---
            Common.Logger.LogInformation("[Engram] [{ThreadKey}] phase 1 — classifying conversation...", threadKey);
            if (!await ClassifyConversationAsync(recentMessages, trigger)) return;

            // --- Phase 2: Fetch ---
            List<string> existingNotes = await brain.GetNoteTitles();
            string existingNotesList   = existingNotes.Count > 0 ? string.Join(", ", existingNotes) : "none";
            string transcript          = BuildTranscript(history);
            string engramThreadKey     = $"engram:{Guid.NewGuid()}";

            if (context is not null)
                await context.RebuildFromTranscriptAsync(threadKey, transcript);

            string contextSummary = context?.GetContext(threadKey) ?? string.Empty;

            Common.Logger.LogInformation("[Engram] [{ThreadKey}] phase 2 — fetch (aware of {Count} existing note(s))",
                threadKey, existingNotes.Count);

            string contextBlock = string.IsNullOrWhiteSpace(contextSummary)
                ? string.Empty
                : $"CONTEXT SUMMARY (use this to resolve all pronouns and identify topics):\n{contextSummary}\n\n";

            HashSet<string> alreadyFetched = new(StringComparer.OrdinalIgnoreCase);

            string initialFetchPrompt =
                "Analyse this conversation and the list of existing notes.\n\n" +
                contextBlock +
                $"CONVERSATION:\n{transcript}\n\n" +
                $"EXISTING NOTES: {existingNotesList}\n\n" +
                "Identify any notes you want to read before extracting — to check for duplicates and to update existing notes. " +
                "Any note you intend to update must be fetched first.\n" +
                "Respond ONLY with: {\"fetch\": [\"Name1\"]} — or {\"fetch\": []} to proceed straight to extraction.";

            string initialRaw    = await PromptThread(engramThreadKey, initialFetchPrompt);
            List<string> toFetch = ParseFetchList(initialRaw).Where(n => !alreadyFetched.Contains(n)).ToList();

            if (toFetch.Count == 0)
                Common.Logger.LogInformation("[Engram] [{ThreadKey}] fetch round 1: no notes requested, proceeding to plan.", threadKey);

            for (int depth = 0; depth < fetchDepth && toFetch.Count > 0; depth++)
            {
                Common.Logger.LogInformation("[Engram] [{ThreadKey}] fetch round {Round}: requesting [{Notes}]",
                    threadKey, depth + 1, string.Join(", ", toFetch));

                StringBuilder sb = new();
                foreach (string name in toFetch)
                {
                    string? noteContent = await brain.GetNote(name);
                    if (noteContent is null) continue;
                    alreadyFetched.Add(name);
                    sb.AppendLine($"--- {name} ---");
                    sb.AppendLine(noteContent);
                    sb.AppendLine("---");
                }

                if (sb.Length == 0) break;

                bool atLimit = depth + 1 >= fetchDepth;
                string deliverPrompt = atLimit
                    ? $"Here are the notes you requested:\n\n{sb}\n\n(Fetch limit reached — proceeding to planning.)"
                    : $"Here are the notes you requested:\n\n{sb}\n\n" +
                      $"Already fetched: {string.Join(", ", alreadyFetched)}.\n" +
                      "If any of those notes reference further notes you need to read (e.g. a [[link]]), request them now. " +
                      "Respond with {\"fetch\": [\"Name\"]} to request more, or {\"fetch\": []} to proceed to planning.";

                string deliverRaw = await PromptThread(engramThreadKey, deliverPrompt);
                toFetch = atLimit
                    ? new List<string>()
                    : ParseFetchList(deliverRaw).Where(n => !alreadyFetched.Contains(n)).ToList();

                if (toFetch.Count == 0)
                    Common.Logger.LogInformation("[Engram] [{ThreadKey}] fetch round {Round}: no further notes requested.", threadKey, depth + 2);
            }

            if (alreadyFetched.Count > 0)
                Common.Logger.LogInformation("[Engram] [{ThreadKey}] fetch complete: read {Count} note(s): [{Notes}]",
                    threadKey, alreadyFetched.Count, string.Join(", ", alreadyFetched));

            // --- Phase 3: Plan ---
            // Ask the model to declare every note it intends to create or edit,
            // with a brief summary. This becomes the manifest for the per-note write loop.
            Common.Logger.LogInformation("[Engram] [{ThreadKey}] phase 3 — planning changes...", threadKey);

            string contextPreamble = string.IsNullOrWhiteSpace(contextSummary)
                ? string.Empty
                : $"Use the context summary to resolve all pronouns:\n{contextSummary}\n\n";

            string planPrompt =
                contextPreamble +
                "Based on the conversation and the notes you have read, list every note you intend to create or edit.\n\n" +
                "For each note provide:\n" +
                "- op: \"add\" for a new note, \"edit\" for updating a note you have already fetched\n" +
                "- name: the DESIRED full note path (e.g. \"People/Family/[REDACT]\") — this is where the note WILL live after the sweep\n" +
                "- summary: 1–2 sentences — the key facts, pronouns, and main links this note will contain\n" +
                "- newName (optional): only set this when an existing note needs to MOVE to a different folder.\n" +
                "  Set name to the CURRENT path and newName to the TARGET path.\n" +
                "  Example: a note currently at People/Ryan that should move to People/Family/Ryan:\n" +
                "  {\"op\": \"edit\", \"name\": \"People/Ryan\", \"newName\": \"People/Family/Ryan\", \"summary\": \"...\"}\n\n" +
                "## HUB NOTES ABSORB LATERAL LINKS\n" +
                "When a thematic hub note exists (e.g. a Family note, a Friends note), individual members should be linked FROM\n" +
                "that hub — not listed individually on [REDACT]'s own note or other personal notes.\n" +
                "- [REDACT]'s note should link to [[[REDACT]'s Family]], not to each family member individually.\n" +
                "- The Family hub (named '[REDACT]'s Family' to avoid ambiguity) carries [[[REDACT]]], [[Peter]], [[Ryan]], etc.\n" +
                "- If a personal note already has a flat list of family/friend/group links, replace them with a single hub link.\n\n" +
                "## FAMILY NOTE NAMING\n" +
                "Family hub notes must be named after the person they belong to (e.g. '[REDACT]'s Family', not just 'Family').\n" +
                "This keeps them unambiguous when multiple people each have their own family graph.\n\n" +
                "Include any Unknown/ notes that should be moved to a proper category (use newName for these).\n" +
                "Include the Conversations/YYYY-MM-DD note.\n\n" +
                "Output ONLY:\n" +
                "{\"plan\": [{\"op\": \"add\", \"name\": \"People/Family/[REDACT]\", \"summary\": \"[REDACT]'s mum, she/her. HR job. Links: Peter, [REDACT], Bamber Bridge.\"}]}\n" +
                "If nothing needs to be stored: {\"plan\": []}";

            string planRaw = await PromptThread(engramThreadKey, planPrompt);
            List<EngramPlanItem> plan = ParsePlanManifest(planRaw);

            if (plan.Count == 0)
            {
                Common.Logger.LogInformation("[Engram] [{ThreadKey}] plan is empty — nothing to store.", threadKey);
                return;
            }

            Common.Logger.LogInformation("[Engram] [{ThreadKey}] plan: {Count} change(s) — [{Notes}]",
                threadKey, plan.Count, string.Join(", ", plan.Select(p =>
                    string.IsNullOrWhiteSpace(p.NewName)
                        ? $"{p.Name} ({p.Op})"
                        : $"{p.Name} → {p.NewName} ({p.Op})")));

            // Snapshot context here — system prompt + conversation + fetched notes + planning exchange.
            // Each per-note write call forks from this cache so context stays constant across the loop.
            IReadOnlyList<ChatMessage> contextCache = GetThreadSnapshot(engramThreadKey);

            // --- Phase 4: Write notes one at a time ---
            Common.Logger.LogInformation("[Engram] [{ThreadKey}] phase 4 — writing {Count} note(s)...", threadKey, plan.Count);

            List<EngramAdd>  allAdds  = new();
            List<EngramEdit> allEdits = new();
            StringBuilder    sweepSummary = new();
            int successCount = 0;
            int failCount    = 0;

            for (int i = 0; i < plan.Count; i++)
            {
                EngramPlanItem item = plan[i];
                Common.Logger.LogInformation("[Engram] [{ThreadKey}] writing ({Current}/{Total}): {Name} ({Op})",
                    threadKey, i + 1, plan.Count, item.Name, item.Op);

                string moveInstruction = string.IsNullOrWhiteSpace(item.NewName)
                    ? string.Empty
                    : $" Move it from its current path to {item.NewName} by setting newName accordingly.";

                string writePrompt =
                    (sweepSummary.Length > 0 ? $"Notes already saved this sweep:\n{sweepSummary}\n" : "") +
                    $"Now write the note: {item.Name}.{moveInstruction}\n\n" +
                    "Output a single JSON object in this exact format:\n" +
                    "For a new note:    {\"add\": [{\"name\": \"Category/NoteName\", \"content\": \"markdown\"}], \"edit\": []}\n" +
                    "For an update:     {\"add\": [], \"edit\": [{\"name\": \"CurrentPath\", \"newName\": \"NewPath\", \"content\": \"full markdown\"}]}\n" +
                    "(Omit newName if the path is not changing. Raw JSON only — no fences, no explanation.)";

                string writeRaw = await PromptAdHocThread(contextCache, writePrompt);
                (List<EngramAdd> noteAdds, List<EngramEdit> noteEdits) = ParseEngramOutput(writeRaw);

                if (noteAdds.Count == 0 && noteEdits.Count == 0)
                {
                    Common.Logger.LogError("[Engram] [{ThreadKey}] failed to parse note ({Current}/{Total}): {Name}. Raw response: {Raw}",
                        threadKey, i + 1, plan.Count, item.Name, writeRaw);
                    failCount++;
                    continue;
                }

                if (noteAdds.Count  > 0) await brain.AddNotes(noteAdds);
                if (noteEdits.Count > 0) await brain.EditNotes(noteEdits);

                allAdds.AddRange(noteAdds);
                allEdits.AddRange(noteEdits);

                sweepSummary.AppendLine($"- {item.Name}: {item.Summary}");
                successCount++;

                string savedName = noteAdds.Count > 0 ? noteAdds[0].NoteName : noteEdits[0].NoteName;
                Common.Logger.LogInformation("[Engram] [{ThreadKey}] saved ({Current}/{Total}): {Name}",
                    threadKey, i + 1, plan.Count, savedName);
            }

            Common.Logger.LogInformation("[Engram] [{ThreadKey}] phase 4 complete: {Success} saved, {Fail} failed.",
                threadKey, successCount, failCount);

            // --- Phase 5: Backlink pass ---
            if (allAdds.Count > 0 || allEdits.Count > 0)
            {
                Common.Logger.LogInformation("[Engram] [{ThreadKey}] phase 5 — backlink pass...", threadKey);
                await ApplyBacklinksAsync(allAdds, allEdits);
            }

            Common.Logger.LogInformation("[Engram] [{ThreadKey}] sweep complete.", threadKey);
        }
        finally
        {
            engramLock.Release();
        }
    }

    private async Task<bool> ClassifyConversationAsync(IReadOnlyList<ChatMessage> recentMessages, string trigger)
    {
        string transcript = BuildTranscript(recentMessages);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            Common.Logger.LogInformation("[Engram] [{Trigger}] no new messages to classify, skipping.", trigger);
            return false;
        }

        object requestBody = new
        {
            model    = ModelString,
            messages = new[]
            {
                new { role = "system", content = "You classify whether a conversation contains information worth storing as a long-term memory.\n<|think_off|>" },
                new { role = "user",   content =
                    "Does the following conversation contain anything worth storing as a long-term memory — " +
                    "personal facts, relationships, events, or world knowledge about the user or their life?\n\n" +
                    "A purely task-focused exchange (coding, debugging, technical problem-solving, or general Q&A) does NOT qualify.\n\n" +
                    $"CONVERSATION:\n{transcript}\n\n" +
                    "Respond with only 'yes' or 'no'." }
            },
            stream      = false,
            max_tokens  = 5,
            temperature = 0.0
        };

        string json = JsonSerializer.Serialize(requestBody);
        HttpRequestMessage request = new(HttpMethod.Post, $"{Endpoint}/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        try
        {
            HttpResponseMessage response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            string responseJson = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(responseJson);
            string answer = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            bool worthStoring = answer.Trim().StartsWith("yes", StringComparison.OrdinalIgnoreCase);
            if (!worthStoring)
                Common.Logger.LogInformation("[Engram] [{Trigger}] classified as task-only, skipping extraction.", trigger);
            return worthStoring;
        }
        catch (Exception ex)
        {
            // On failure, fall through to extraction rather than silently dropping memories.
            Common.Logger.LogWarning("[Engram] Classification failed ({Error}), proceeding with extraction.", ex.Message);
            return true;
        }
    }

    private static string BuildTranscript(IReadOnlyList<ChatMessage> history)
    {
        StringBuilder sb = new();
        foreach (ChatMessage msg in history)
        {
            if (msg.Role == "system") continue;
            string speaker = msg.Role == "user" ? "User" : "ARI";
            sb.AppendLine($"{speaker}: {msg.Content}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extracts every [[NoteTitle]] reference from markdown content.
    /// </summary>
    private static IEnumerable<string> ParseLinks(string markdown)
        => Regex.Matches(markdown, @"\[\[([^\]]+)\]\]")
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For every [[link]] written into a newly added or edited note, adds a return link
    /// in the target note if one is not already present. Runs in parallel for speed.
    /// </summary>
    private async Task ApplyBacklinksAsync(List<EngramAdd> adds, List<EngramEdit> edits)
    {
        // Build (sourceName, targetName) pairs from all new/edited content.
        // sourceName = the bare note title (last path segment).
        HashSet<(string source, string target)> pairs = new();

        foreach (EngramAdd add in adds)
        {
            string source = add.NoteName.Split('/')[^1];
            foreach (string target in ParseLinks(add.Content))
                pairs.Add((source, target));
        }

        foreach (EngramEdit edit in edits)
        {
            string source = (edit.NewNoteName ?? edit.NoteName).Split('/')[^1];
            foreach (string target in ParseLinks(edit.Content))
                pairs.Add((source, target));
        }

        if (pairs.Count == 0) return;

        Common.Logger.LogInformation("[Engram] Backlink pass: {Count} candidate pair(s).", pairs.Count);
        await Task.WhenAll(pairs.Select(p => brain.AddBacklink(p.target, p.source)));
    }

    private record EngramPlanItem(string Op, string Name, string Summary, string? NewName = null);

    private static List<EngramPlanItem> ParsePlanManifest(string raw)
    {
        try
        {
            raw = raw.Trim();
            int start = raw.IndexOf('{');
            if (start >= 0) raw = raw[start..];
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("plan", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
                return new();

            List<EngramPlanItem> items = new();
            foreach (JsonElement el in arr.EnumerateArray())
            {
                string? op      = el.GetString("op");
                string? name    = el.GetString("name");
                string? summary = el.GetString("summary");
                string? newName = el.GetString("newName");
                if (!string.IsNullOrWhiteSpace(op) && !string.IsNullOrWhiteSpace(name))
                    items.Add(new EngramPlanItem(op, name, summary ?? string.Empty, newName));
            }
            return items;
        }
        catch (Exception ex)
        {
            Common.Logger.LogError("[Engram] Failed to parse plan manifest: {Error}. Raw: {Raw}", ex.Message, raw);
            return new();
        }
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

    private static (List<EngramAdd> adds, List<EngramEdit> edits) ParseEngramOutput(string raw)
    {
        raw = Regex.Replace(raw, @"```[a-zA-Z]*\n?", "").Trim('`').Trim();

        int start = raw.IndexOf('{');
        if (start >= 0) raw = raw[start..];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;

            List<EngramAdd> adds = new();
            if (root.TryGetProperty("add", out JsonElement addArr) && addArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in addArr.EnumerateArray())
                {
                    string? noteName = el.GetString("name");
                    string? content  = el.GetString("content");
                    if (!string.IsNullOrWhiteSpace(noteName) && content is not null)
                        adds.Add(new EngramAdd { NoteName = noteName, Content = content });
                }
            }

            List<EngramEdit> edits = new();
            if (root.TryGetProperty("edit", out JsonElement editArr) && editArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in editArr.EnumerateArray())
                {
                    string? noteName    = el.GetString("name");
                    string? newNoteName = el.GetString("newName");
                    string? content     = el.GetString("content");
                    if (!string.IsNullOrWhiteSpace(noteName) && content is not null)
                        edits.Add(new EngramEdit { NoteName = noteName, NewNoteName = newNoteName, Content = content });
                }
            }

            return (adds, edits);
        }
        catch (Exception ex)
        {
            Common.Logger.LogError("[Engram] Failed to parse note output: {Error}. Raw: {Raw}", ex.Message, raw);
            return (new(), new());
        }
    }
}

file static class JsonElementExtensions
{
    internal static string? GetString(this JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

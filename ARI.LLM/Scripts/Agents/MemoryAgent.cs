using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Serialization;
using ARI.Brain;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

// Base for the tool-driven memory agents (Engram, Refactor, Curiosity). Unlike the old plan-then-write
// approach it replaced — which emitted
// add/edit/merge/delete JSON and applied it in one batch — a MemoryAgent treats the vault as a
// filesystem and edits it through real tools (file + git + graph), one committed change at a time.
//
// The core is a graph WALK, not a folder loop: each epoch seeds a fresh internal thread with one node's
// neighbourhood skeleton and lets the model read, think, and make ONE logical change, review it, and
// commit it. Seeds are the highest-degree nodes (where sprawl lives), re-ranked every epoch. The walk
// stops after MaxEpochs or once a run of epochs makes no change (converged). Every epoch runs on an
// Internal thread — invisible in chat, but its full reasoning/tool trace flows to the DTI.
internal abstract class MemoryAgent : Agent
{
    // ── Walk knobs (const so they're easy to tune; raise as the vault grows) ──────────────
    protected const int SEED_COUNT           = 50;   // how many top-degree nodes are candidate seeds
    protected const int WALK_DEPTH            = 4;    // BFS hops out from a seed
    protected const int WALK_CAP              = 1000; // max nodes in a neighbourhood skeleton
    protected const int DEFAULT_MAX_EPOCHS    = 100;  // hard cap on epochs (one commit each)
    protected const int CONVERGED_AFTER       = 3;    // consecutive "no change needed" epochs ⇒ converged, stop early
    protected const int STALL_LIMIT           = 5;    // consecutive stalled epochs (no act, no "no change") ⇒ bail

    // The taxonomy/hub/type/linking rulebook the memory agents obey, injected into every turn's system
    // context so it stays the single source of truth (the config systemPrompt carries only the persona/role).
    // Shared by all three; Curiosity opts out via UseGraphRulebook.
    // Nullable: the control panel writes null for agents that never set it, and a non-nullable bool
    // makes that a fatal deserialise error at startup.
    [JsonPropertyName("useGraphRulebook")] public bool? UseGraphRulebook { get; init; }

    internal override string PersistentContext(Thread thread)
        => (UseGraphRulebook ?? true) ? "\n\n" + SharedPrompts.GraphRulebook : "";

    // ── Per-turn guard state ──────────────────────────────────────────────────────────────
    // Distinct notes the model may read in one epoch before it's pushed to act. The model fills its whole
    // thinking budget on EVERY step, so each extra read is another full-budget think — reads are the main
    // time sink. Bounding them (and blocking re-reads) is what actually shortens an epoch.
    protected const int READ_CEILING = 4;
    // Hard cap on WORK tool calls per epoch (reads + mutations + search/neighbours). The git ritual
    // (status/diff/log/commit) is excluded so the commit at the end of a clean epoch always lands. Anything
    // past this many work calls is a spiral, so we END the turn gracefully (ShouldBreak breaks the
    // loop WITHOUT withdrawing tools mid-generation, which is what triggered the earlier text-fallback aborts).
    // This bound exists for the single-change Refactor epoch; Engram seeds a whole conversation and needs to
    // recon several entities before writing, so it overrides this to null (no ceiling).
    protected virtual int? EpochToolCeiling => 8;

    // When true, the walk ranks seeds by least-recently-refactored (never-refactored first), degree as
    // the tiebreak, and stamps each seed's title when its epoch ends. Only Refactor opts in.
    protected virtual bool TrackLastRefactored => false;

    protected sealed class MemoryTurnState : ToolTurnState
    {
        public bool DiffViewedSinceWrite;                                         // a git_diff since the last mutation
        public bool Committed;                                                     // a git_commit landed this epoch
        public int  ToolCalls;                                                     // total tool calls this epoch (logging)
        public int  WorkCalls;                                                     // read/mutation/search calls — drives the breaker
        public readonly HashSet<string> ReadPaths = new(StringComparer.OrdinalIgnoreCase);  // notes read this epoch
    }

    protected override bool ShouldBreak(Thread thread, ToolTurnState state, bool productiveBatch)
    {
        if (state is not MemoryTurnState m) return false;
        // Commit landed — the epoch's work is DONE. End immediately instead of doing one more LLM turn: with
        // tools withdrawn that turn just burns the full thinking budget (~4 min) reasoning about nothing before
        // producing a throwaway final message. Breaking here is the single biggest per-epoch time saving.
        if (m.Committed && StopAfterCommit)
        {
            Shared.Logger.LogInformation("[{Agent}] commit landed — ending epoch (skipping the empty post-commit turn).", Name);
            return true;
        }
        if (EpochToolCeiling is int ceiling && m.WorkCalls >= ceiling)
        {
            Shared.Logger.LogWarning("[{Agent}] circuit breaker: {N} work tool calls this epoch — ending turn.", Name, m.WorkCalls);
            return true;
        }
        return false;
    }
    protected override ToolTurnState NewTurnState() => new MemoryTurnState();

    // Dump each step's raw reasoning to reasoning-{Name}.log so training can read the walk's actual thinking.
    protected override bool LogReasoning => true;

    protected static string? ArgPath(string argsJson)
    {
        try { using System.Text.Json.JsonDocument d = System.Text.Json.JsonDocument.Parse(argsJson);
              return d.RootElement.TryGetProperty("path", out System.Text.Json.JsonElement p) ? p.GetString()?.Trim() : null; }
        catch { return null; }
    }

    // The walk (Refactor) ends a turn after one committed change — one logical change per epoch. Engram
    // seeds a turn with a whole conversation and places several memories, so it overrides this to false.
    protected virtual bool StopAfterCommit => true;

    // The live thread registry the API/DTI enumerates. Set at wire-up so a walk's (internal) parent thread
    // is discoverable — the DTI then drills into its per-epoch children and their reasoning traces. Without
    // this the walk runs but is invisible to inspection.
    [System.Text.Json.Serialization.JsonIgnore] internal ConcurrentDictionary<string, Thread>? Registry { get; set; }

    // Poke callback (LLMModule.NotifyWatchers) — fired per stream delta so the DTI auto-refreshes the
    // walk's thread while it thinks, instead of only when the panel is re-selected. Set at wire-up.
    [System.Text.Json.Serialization.JsonIgnore] internal Action<string>? Notify { get; set; }

    // Register a walk/placement parent thread so it surfaces in the DTI (GET /threads?includeInternal=true),
    // and poke watchers so the panel refreshes live as epochs stream.
    protected void PublishForInspection(Thread parent)
    {
        Registry?.TryAdd(parent.Key, parent);
        parent.RaiseUpdated();
    }

    // Always review the diff before committing; end the epoch as soon as one change is committed.
    protected override string? BeforeTool(Thread thread, ToolTurnState state, string toolName, string callId, string argsJson)
    {
        if (state is not MemoryTurnState m) return null;

        if (toolName == "git_commit" && !m.DiffViewedSinceWrite)
            return "[System: review the change with git_diff before committing so you commit exactly what you intended.]";

        // Block wholesale rewrites of an EXISTING note — write_file over a live note repeatedly destroyed its
        // YAML frontmatter/structure (the model then burned its whole epoch trying to repair the damage).
        // write_file is only for creating a NEW note; to change an existing one, edit the specific lines.
        if (toolName == "write_file")
        {
            string? wpath = ArgPath(argsJson);
            if (wpath is not null)
            {
                string abs = System.IO.Path.Combine(BrainModule.VaultRoot, wpath);
                if (System.IO.File.Exists(abs))
                {
                    Shared.Logger.LogInformation("[{Agent}] write guard: blocked wholesale write_file of existing note {Path}.", Name, wpath);
                    return $"[System: {wpath} already exists — do NOT write_file over it (that replaces the whole file and destroys its frontmatter/structure). Make your change with edit_file on the specific lines instead. write_file is only for a note that does not exist yet.]";
                }
            }
        }

        // Curb over-reading: block re-reads (content is already in context) and, past the ceiling, stop reads
        // entirely and push the model to act. Each read is another full-budget think, so this is the main
        // lever on epoch length.
        if (toolName == "read_file")
        {
            string? path = ArgPath(argsJson);
            if (path is not null && m.ReadPaths.Contains(path))
            {
                Shared.Logger.LogInformation("[{Agent}] read guard: blocked RE-READ of {Path}.", Name, path);
                return $"[System: you already read {path} this epoch — its content is above in the conversation. Do not re-read it; use what you have.]";
            }
            if (m.ReadPaths.Count >= READ_CEILING)
            {
                Shared.Logger.LogInformation("[{Agent}] read guard: ceiling hit ({N} reads) — forcing action.", Name, m.ReadPaths.Count);
                return $"[System: you have read {m.ReadPaths.Count} notes — enough context. Make the ONE change now (edit_file/write_file/move_file/delete_file/merge_notes), then git_diff and git_commit; or reply 'no change'. Do not read more.]";
            }
        }
        return null;
    }

    protected override string AfterTool(Thread thread, ToolTurnState state, string toolName, string argsJson, string result)
    {
        if (state is MemoryTurnState m)
        {
            m.ToolCalls++;
            // The git ritual (status/diff/log/commit) is FREE — it must never trip the breaker, or a clean
            // read→edit→git_diff→git_commit epoch gets killed before it can commit (throwing away good work).
            bool gitRitual = toolName is "git_status" or "git_diff" or "git_log" or "git_commit";
            if (!gitRitual) m.WorkCalls++;
            string snip = result.Length > 90 ? result[..90].Replace("\n", " ") : result.Replace("\n", " ");
            Shared.Logger.LogInformation("[{Agent}] tool #{N} {Tool} → {Result}", Name, m.ToolCalls, toolName, snip);
            if (toolName is "write_file" or "edit_file" or "move_file" or "delete_file" or "merge_notes")
            {
                m.DiffViewedSinceWrite = false;
                // The file's line numbers just shifted, so ONE refresh read of it is legitimate again (the Coder
                // rule). The edit result already hands back the updated numbered content, so this is a safety net.
                string? edited = ArgPath(argsJson);
                if (edited is not null) m.ReadPaths.Remove(edited);
            }
            else if (toolName == "read_file")
            {
                string? path = ArgPath(argsJson);   // only actual (non-blocked) reads reach here
                if (path is not null) m.ReadPaths.Add(path);
            }
            else if (toolName == "git_diff")
                m.DiffViewedSinceWrite = true;
            else if (toolName == "git_commit" && result.StartsWith("Committed", StringComparison.Ordinal))
            {
                m.Committed = true;
                if (StopAfterCommit) m.toolsCancelled = true;   // walk: one logical change per epoch
            }
        }
        return result;
    }

    // ── Tool registration ─────────────────────────────────────────────────────────────────
    // Filesystem + git tools rooted at the vault, plus the graph/curiosity tools. No memory-specific
    // file tools — the vault is edited through the same file tools the coding agent uses.
    // Virtual so a read-only walker (Curiosity) can register a navigation + curiosity subset instead.
    protected virtual void RegisterTools(Thread thread, string persistentDir, CancellationToken ct)
    {
        string root = BrainModule.VaultRoot;
        thread.ProjectRoot = root;
        thread.IsBrainVault = true;
        thread.Ct = ct;

        ServerFileSystem fs = new(root, ct, brainVault: true);
        new ReadFile(fs).Register(thread);
        new WriteFile(fs).Register(thread);
        new EditFile(fs).Register(thread);
        new MoveFile(fs).Register(thread);
        new DeleteFile(fs).Register(thread);
        new ListDirectory(fs).Register(thread);
        // search_files / find_files over the vault redirect to search_brain (alias-aware) — see ServerFileSystem.
        new SearchBrain().Register(thread);
        new SearchFiles(fs).Register(thread);
        new FindFiles(fs).Register(thread);

        // Git tools are used ~once per session (issue #126) — deferred behind request_tools("git_tools")
        // instead of always sitting in context, resolved generically via ToolFactories (agent-agnostic —
        // see Thread.ProjectRoot). PreloadedTools can still name "git_tools" in Agents.json to keep them
        // warm/eager for an agent that calls them almost every turn.
        new ListTools().Register(thread);
        new RequestTools(thread).Register(thread);
        if (PreloadedTools?.Contains("git_tools", StringComparer.OrdinalIgnoreCase) == true)
            ToolFactories.LoadGroup("git_tools", thread);

        new Neighbours().Register(thread);
        new MergeNotesTool().Register(thread);
        // Curiosity-recording is Engram's (and the Curiosity agent's) job, not the tidy walk's. Refactor
        // turns these off so it stays tidy-only and isn't tempted by tools it never uses.
        if (IncludeCuriosityTools)
        {
            new AddCuriosity(persistentDir).Register(thread);
            new RemoveCuriosity(persistentDir).Register(thread);
            new ListCuriosities(persistentDir).Register(thread);
        }
    }

    // Whether the base tool set includes the add/remove/list-curiosity tools. Refactor overrides to false.
    protected virtual bool IncludeCuriosityTools => true;

    // ── The walk ──────────────────────────────────────────────────────────────────────────
    // Seeds by degree, re-ranked each epoch; skips seeds visited since the last full pass so consecutive
    // epochs don't re-walk the same region. `task` is the agent-specific instruction (tidy / place a
    // memory). Returns a short summary. Backs nothing up — git history is the safety net.
    // convergeOnNoChange: Refactor/Engram STOP once the region is clean (CONVERGED_AFTER quiet epochs).
    // Curiosity sets this false — "no curiosity here" is not convergence (curiosities are sparse), so it
    // keeps exploring across neighbourhoods until the epoch cap, giving broad coverage each run.
    protected async Task<string> RunWalk(
        Thread parent, string threadKey, string task, string persistentDir,
        int maxEpochs, CancellationToken ct, Func<string, Task>? onDelta, bool convergeOnNoChange = true)
    {
        int epoch = 0, changes = 0, noChange = 0, stalled = 0;
        HashSet<string> visitedThisPass = new(StringComparer.OrdinalIgnoreCase);
        // Refactor rotates through the vault least-recently-refactored first; other walks pass null and
        // keep the plain top-degree seed order.
        RefactorLog? refactorLog = TrackLastRefactored ? new RefactorLog(persistentDir) : null;
        PublishForInspection(parent);   // surface the walk in the DTI before the first epoch

        while (epoch < maxEpochs && (!convergeOnNoChange || noChange < CONVERGED_AFTER) && stalled < STALL_LIMIT)
        {
            ct.ThrowIfCancellationRequested();
            BrainModule.Index();

            // Default: top-degree seeds. Refactor instead ranks the WHOLE vault by least-recently-
            // refactored (never-refactored = DateTime.MinValue, so it sorts first), with the SQL
            // degree-DESC order preserved as the tiebreak because OrderBy is a stable sort.
            List<Note> seeds = refactorLog is null
                ? BrainModule.TopDegreeSeeds(SEED_COUNT)
                : BrainModule.AllSeedsByDegree().OrderBy(s => refactorLog.SortKey(s.Title)).ToList();
            if (seeds.Count == 0) break;

            Note? seed = seeds.FirstOrDefault(s => !visitedThisPass.Contains(s.Title));
            if (seed is null) { visitedThisPass.Clear(); seed = seeds[0]; }  // full pass done — start another
            visitedThisPass.Add(seed.Title);

            string skeleton = BrainModule.Skeleton(seed.Title, WALK_DEPTH, WALK_CAP) ?? "";

            Thread epochThread = new(ThreadPipeline.Dialogue, $"{threadKey}#epoch{epoch}:{Guid.NewGuid():N}")
                { Internal = true, Parent = parent, Label = $"epoch {epoch}: {seed.Title}" };
            parent.AddChild(epochThread);
            parent.RaiseUpdated();   // poke the DTI to pick up the new epoch child
            RegisterTools(epochThread, persistentDir, ct);

            Shared.Logger.LogInformation("[{Agent}] epoch {Epoch} — seed '{Seed}' ({Nodes} nodes in view).",
                Name, epoch, seed.Title, skeleton.Count(c => c == '\n'));

            // Poke the DTI (via the registered parent's key) on every delta so its thinking streams live.
            Func<string, Task> live = async delta => { Notify?.Invoke(threadKey); if (onDelta is not null) await onDelta(delta); };

            EpochOutcome outcome;
            try
            {
                await Prompt(epochThread, BuildEpochPrompt(task, seed.Title, skeleton),
                    new PromptOptions { Username = "system", Ct = ct, OnDelta = live });
                outcome = AssessEpoch(epochThread);
            }
            catch (OperationCanceledException) { throw; }   // a real cancel ends the whole walk
            catch (Exception ex)
            {
                // Isolate epoch failures (e.g. a tool-call fallback loop): a single bad epoch must SKIP its
                // seed and let the walk continue, not abort the whole run. Counts as a stall.
                Shared.Logger.LogWarning("[{Agent}] epoch {Epoch} failed ({Error}) — skipping seed '{Seed}'.", Name, epoch, ex.Message, seed.Title);
                outcome = EpochOutcome.Stalled;
            }

            switch (outcome)
            {
                case EpochOutcome.Committed: changes++; noChange = 0; stalled = 0; break;
                case EpochOutcome.NoChange:  noChange++;              stalled = 0; break;
                // A stalled epoch (model errored or thought itself into an empty turn) is NOT evidence the
                // graph is clean, so it must not count toward convergence — only guard against an endless loop.
                case EpochOutcome.Stalled:   stalled++; Shared.Logger.LogWarning("[{Agent}] epoch {Epoch} stalled.", Name, epoch); break;
            }
            // Stamp the seed as refactored regardless of outcome (committed OR no-change) so a clean
            // note isn't re-picked ahead of never-refactored ones on the next run. A stalled epoch is
            // NOT stamped — it never really examined the note, so it stays eligible.
            if (outcome != EpochOutcome.Stalled) refactorLog?.Touch(seed.Title);
            epoch++;
        }

        string reason = noChange >= CONVERGED_AFTER ? "converged"
                      : stalled  >= STALL_LIMIT     ? $"halted after {STALL_LIMIT} stalled epochs"
                      :                               "epoch cap reached";
        string summary = $"Memory walk complete ({reason}) — {epoch} epoch(s), {changes} change(s) committed.";
        // Log it (not just return it) so external tooling / the eval harness can detect the walk's end from
        // the log — the returned string alone never reaches ARI.log.
        Shared.Logger.LogInformation("[{Agent}] {Summary}", Name, summary);
        return summary;
    }

    // The per-epoch prompt: the seed's neighbourhood skeleton + the agent's task + the one-change-then-
    protected virtual string BuildEpochPrompt(string task, string seedTitle, string skeleton)
        => SharedPrompts.Epoch(
            ("seedTitle", seedTitle),
            ("skeleton",  skeleton.Length > 0 ? skeleton : "(no connections)"),
            ("task",      task));

    protected enum EpochOutcome { Committed, NoChange, Stalled }

    // Classify how an epoch ended: a successful git_commit ⇒ Committed; an explicit "no change" reply ⇒
    // NoChange (genuine no-op, counts toward convergence); anything else (an error, or the model thinking
    // itself into an empty turn without acting) ⇒ Stalled, which must NOT count as the graph being clean.
    // Virtual so a read-only walker (Curiosity) can treat "added a curiosity" as the productive outcome.
    protected virtual EpochOutcome AssessEpoch(Thread thread)
    {
        bool committed = thread.History.OfType<Response>()
            .SelectMany(r => r.Trace ?? Enumerable.Empty<TraceStep>())
            .Any(s => s.Kind == "tool_result" && s.Name == "git_commit"
                      && (s.Text?.StartsWith("Committed", StringComparison.Ordinal) ?? false));
        if (committed) return EpochOutcome.Committed;

        string finalText = thread.History.OfType<Response>().LastOrDefault()?.ContentText ?? string.Empty;
        return System.Text.RegularExpressions.Regex.IsMatch(finalText, @"\bno\s+change(s)?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? EpochOutcome.NoChange
            : EpochOutcome.Stalled;
    }

    // True when this epoch's trace contains a successful call to `tool` — helper for AssessEpoch overrides.
    protected static bool EpochCalled(Thread thread, string tool) =>
        thread.History.OfType<Response>()
            .SelectMany(r => r.Trace ?? Enumerable.Empty<TraceStep>())
            .Any(s => s.Kind == "tool_result" && s.Name == tool);
}

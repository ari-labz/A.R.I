using System.Collections.Concurrent;
using System.Text;
using ARI.Brain;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

// Base for the tool-driven memory agents (Refactor, Engram). Unlike the old BrainAgent — which emitted
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
    protected const int WALK_DEPTH            = 6;    // BFS hops out from a seed
    protected const int WALK_CAP              = 1000; // max nodes in a neighbourhood skeleton
    protected const int DEFAULT_MAX_EPOCHS    = 100;  // hard cap on epochs (one commit each)
    protected const int CONVERGED_AFTER       = 3;    // consecutive no-change epochs ⇒ converged, stop early
    // ~1 change per minute: the thinking ceiling per epoch. Configurable — raise for deeper reasoning.
    protected const int THINK_SECONDS_PER_EPOCH = 60;

    // The taxonomy/hub/type/linking rulebook both memory agents obey, injected into every turn's system
    // context so it stays the single source of truth (the config systemPrompt carries only the persona/role).
    internal override string BuildPersistentContext(Thread thread) => "\n\n" + BrainRulebook.RULES;

    // ── Per-turn guard state ──────────────────────────────────────────────────────────────
    protected sealed class MemoryTurnState : ToolTurnState
    {
        public bool DiffViewedSinceWrite;  // a git_diff has been seen since the last file mutation
        public bool Committed;             // a git_commit landed this epoch
    }
    protected override ToolTurnState CreateToolTurnState() => new MemoryTurnState();

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
    protected override string? PreToolGuard(Thread thread, ToolTurnState state, string toolName, string callId, string argsJson)
    {
        if (toolName == "git_commit" && state is MemoryTurnState m && !m.DiffViewedSinceWrite)
            return "[System: review the change with git_diff before committing so you commit exactly what you intended.]";
        return null;
    }

    protected override string PostToolProcess(Thread thread, ToolTurnState state, string toolName, string argsJson, string result)
    {
        if (state is MemoryTurnState m)
        {
            if (toolName is "write_file" or "edit_file" or "move_file" or "delete_file" or "merge_notes")
                m.DiffViewedSinceWrite = false;
            else if (toolName == "git_diff")
                m.DiffViewedSinceWrite = true;
            else if (toolName == "git_commit" && result.StartsWith("Committed", StringComparison.Ordinal))
            {
                m.Committed = true;
                if (StopAfterCommit) m.ForceNoMoreTools = true;   // walk: one logical change per epoch
            }
        }
        return result;
    }

    // ── Tool registration ─────────────────────────────────────────────────────────────────
    // Filesystem + git tools rooted at the vault, plus the graph/curiosity tools. No memory-specific
    // file tools — the vault is edited through the same file tools the coding agent uses.
    protected void RegisterTools(Thread thread, string persistentDir, CancellationToken ct)
    {
        string root = BrainModule.VaultRoot;
        ServerFileSystem fs = new(root, ct);
        new ReadFile(fs).Register(thread);
        new WriteFile(fs).Register(thread);
        new EditFile(fs).Register(thread);
        new MoveFile(fs).Register(thread);
        new DeleteFile(fs).Register(thread);
        new ListDirectory(fs).Register(thread);
        new SearchFiles(fs).Register(thread);
        new FindFiles(fs).Register(thread);

        new GitStatus(root).Register(thread);
        new GitDiff(root).Register(thread);
        new GitLog(root).Register(thread);
        new GitCommit(root).Register(thread);

        new Neighbours().Register(thread);
        new MergeNotesTool().Register(thread);
        new AddCuriosity(persistentDir).Register(thread);
        new RemoveCuriosity(persistentDir).Register(thread);
        new ListCuriosities(persistentDir).Register(thread);
    }

    // ── The walk ──────────────────────────────────────────────────────────────────────────
    // Seeds by degree, re-ranked each epoch; skips seeds visited since the last full pass so consecutive
    // epochs don't re-walk the same region. `task` is the agent-specific instruction (tidy / place a
    // memory). Returns a short summary. Backs nothing up — git history is the safety net.
    protected async Task<string> RunWalk(
        Thread parent, string threadKey, string task, string persistentDir,
        int maxEpochs, CancellationToken ct, Func<string, Task>? onDelta)
    {
        int epoch = 0, changes = 0, noChange = 0;
        HashSet<string> visitedThisPass = new(StringComparer.OrdinalIgnoreCase);
        PublishForInspection(parent);   // surface the walk in the DTI before the first epoch

        while (epoch < maxEpochs && noChange < CONVERGED_AFTER)
        {
            ct.ThrowIfCancellationRequested();
            BrainModule.Index();

            List<Note> seeds = BrainModule.TopDegreeSeeds(SEED_COUNT);
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
            await SendPrompt(epochThread, BuildEpochPrompt(task, seed.Title, skeleton), "system",
                ct: ct, thinkSeconds: THINK_SECONDS_PER_EPOCH, onDelta: live);

            if (EpochCommitted(epochThread)) { changes++; noChange = 0; }
            else noChange++;
            epoch++;
        }

        string reason = noChange >= CONVERGED_AFTER ? "converged" : "epoch cap reached";
        return $"Memory walk complete ({reason}) — {epoch} epoch(s), {changes} change(s) committed.";
    }

    // The per-epoch prompt: the seed's neighbourhood skeleton + the agent's task + the one-change-then-
    // commit contract. The rules/persona live in the agent's SystemPrompt (config); this is operational.
    protected virtual string BuildEpochPrompt(string task, string seedTitle, string skeleton)
    {
        StringBuilder sb = new();
        sb.AppendLine($"You are walking the memory graph. Current neighbourhood around '{seedTitle}'");
        sb.AppendLine("(each block: full path + [type], then its inbound '<' and outbound '>' connections):");
        sb.AppendLine();
        sb.AppendLine(skeleton.Length > 0 ? skeleton : "(no connections)");
        sb.AppendLine();
        sb.AppendLine(task);
        sb.AppendLine();
        sb.AppendLine("Make AT MOST ONE logical change this turn (it may touch several files if they are one " +
                      "coherent change, e.g. extracting a section into a new note). Before editing a note, read it " +
                      "(read_file) and check its history (git_log) so you don't undo earlier work. When done, review " +
                      "with git_diff, then git_commit with a clear subject and a body explaining WHY. If nothing here " +
                      "needs changing, do NOT force a change — reply 'no change' and stop.");
        return sb.ToString();
    }

    // An epoch counts as a change iff a git_commit tool call succeeded in it (checked from the DTI trace —
    // no extra git code path).
    private static bool EpochCommitted(Thread thread) =>
        thread.History.OfType<Response>()
            .SelectMany(r => r.Trace ?? Enumerable.Empty<TraceStep>())
            .Any(s => s.Kind == "tool_result" && s.Name == "git_commit"
                      && (s.Text?.StartsWith("Committed", StringComparison.Ordinal) ?? false));
}

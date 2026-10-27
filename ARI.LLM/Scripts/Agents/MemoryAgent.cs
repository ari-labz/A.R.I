using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ARI.BrainVault;
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
    protected const int WALK_DEPTH            = 2;    // BFS hops out from a seed
    protected const int WALK_CAP              = 50;   // max nodes in a neighbourhood skeleton
    protected const int DEFAULT_MAX_EPOCHS    = 100;  // hard cap on epochs (one commit each)
    protected const int CONVERGED_AFTER       = 3;    // consecutive "no change needed" epochs ⇒ converged, stop early
    protected const int STALL_LIMIT           = 5;    // consecutive stalled epochs (no act, no "no change") ⇒ bail

    // The write-guard below predates Engram's 3-stage redesign — it was built for Refactor's line-edit
    // walk, where a blind write_file over a live note (no prior read) really did destroy frontmatter.
    // Engram's Save step always reads the existing note first and its own prompt promises write_file
    // as the only tool, so the guard's premise doesn't hold there. Refactor (and Curiosity, inert by
    // default) keep the protection they actually need; only Engram opts out.
    internal virtual bool BlocksWriteOverExisting => true;

    // ── Per-turn guard state ──────────────────────────────────────────────────────────────
    // Distinct notes the model may read in one epoch before it's pushed to act. The model fills its whole
    // thinking budget on EVERY step, so each extra read is another full-budget think — reads are the main
    // time sink. Bounding them (and blocking re-reads) is what actually shortens an epoch.
    internal virtual int ReadCeiling => 3;
    // Hard cap on WORK tool calls per epoch (reads + mutations + search/neighbours). The git ritual
    // (status/diff/log/commit) is excluded so the commit at the end of a clean epoch always lands. Anything
    // past this many work calls is a spiral, so we END the turn gracefully (ShouldBreak breaks the
    // loop WITHOUT withdrawing tools mid-generation, which is what triggered the earlier text-fallback aborts).
    // This bound exists for the single-change Refactor epoch; Engram seeds a whole conversation and needs to
    // recon several entities before writing, so it overrides this to null (no ceiling).
    internal virtual int? EpochToolCeiling => 8;

    internal sealed class MemoryTurnState : ToolTurnState
    {
        public bool Cancelled;                                                     // set after commit when StopAfterCommit; withdraws tools for the rest of the turn
        public bool DiffViewedSinceWrite;                                         // a git diff since the last mutation
        public bool Committed;                                                     // a git commit landed this epoch
        public int  ToolCalls;                                                     // total tool calls this epoch (logging)
        public int  WorkCalls;                                                     // read/mutation/search calls — drives the breaker
        public readonly HashSet<string> ReadPaths = new(StringComparer.OrdinalIgnoreCase);  // notes read this epoch
    }

    internal override bool ToolsCancelled(ToolTurnState state)
        => state is MemoryTurnState m && m.Cancelled;

    internal override bool ShouldBreak(Thread thread, ToolTurnState state, bool productiveBatch)
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
    internal override ToolTurnState NewTurnState() => new MemoryTurnState();

    // Dump each step's raw reasoning to reasoning-{Name}.log so training can read the walk's actual thinking.
    internal override bool LogReasoning => true;

    protected static string? ArgPath(string argsJson)
    {
        try { using JsonDocument d = JsonDocument.Parse(argsJson);
              return d.RootElement.TryGetProperty("path", out JsonElement p) ? p.GetString()?.Trim() : null; }
        catch { return null; }
    }

    /// <summary>The git subcommand of a `git` tool call (status/diff/log/commit/…), or null for any other tool.</summary>
    protected static string? GitCommand(string toolName, string argsJson)
    {
        if (toolName != "git") return null;
        try { using JsonDocument d = JsonDocument.Parse(argsJson);
              return d.RootElement.TryGetProperty("command", out JsonElement c) ? c.GetString()?.Trim() : null; }
        catch { return null; }
    }

    // The walk (Refactor) ends a turn after one committed change — one logical change per epoch. Engram
    // seeds a turn with a whole conversation and places several memories, so it overrides this to false.
    internal virtual bool StopAfterCommit => true;

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
    internal override string? OnToolCall(Thread thread, ToolTurnState state, string toolName, string callId, string argsJson)
    {
        if (state is not MemoryTurnState m) return null;

        if (GitCommand(toolName, argsJson) == "commit" && !m.DiffViewedSinceWrite)
            return "[System: review the change with git diff before committing so you commit exactly what you intended.]";

        // Block wholesale rewrites of an EXISTING note — write_file over a live note repeatedly destroyed its
        // YAML frontmatter/structure (the model then burned its whole epoch trying to repair the damage).
        // write_file is only for creating a NEW note; to change an existing one, edit the specific lines.
        if (toolName == "write_file" && BlocksWriteOverExisting)
        {
            string? wpath = ArgPath(argsJson);
            if (wpath is not null)
            {
                string abs = System.IO.Path.Combine(Brain.VaultRoot, wpath);
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
            if (m.ReadPaths.Count >= ReadCeiling)
            {
                Shared.Logger.LogInformation("[{Agent}] read guard: ceiling hit ({N} reads) — forcing action.", Name, m.ReadPaths.Count);
                return $"[System: you have read {m.ReadPaths.Count} notes — enough context. Make the ONE change now (edit_file/write_file/move_file/delete_file/merge_notes), then git diff and git commit; or reply 'no change'. Do not read more.]";
            }
        }
        return null;
    }

    internal override string OnToolResult(Thread thread, ToolTurnState state, string toolName, string argsJson, string result)
    {
        if (state is MemoryTurnState m)
        {
            m.ToolCalls++;
            // The git ritual (status/diff/log/commit) is FREE — it must never trip the breaker, or a clean
            // read→edit→git diff→git commit epoch gets killed before it can commit (throwing away good work).
            string? gitCommand = GitCommand(toolName, argsJson);
            bool gitRitual = gitCommand is "status" or "diff" or "log" or "commit";
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
                // A "[Read window]" (file too big, pick a range) or "[Error...]" response delivers no
                // content — only a real "[file: ...]" result actually put the note in context. Marking
                // ReadPaths on those failures used to permanently lock the model out of ever reading the
                // file (the guard above blocks any path already in ReadPaths), so a big note it never
                // successfully read became unreadable for the rest of the epoch.
                string? path = ArgPath(argsJson);
                if (path is not null && result.TrimStart().StartsWith("[file:", StringComparison.Ordinal))
                    m.ReadPaths.Add(path);
            }
            else if (gitCommand == "diff")
                m.DiffViewedSinceWrite = true;
            else if (gitCommand == "commit" && result.StartsWith("Committed", StringComparison.Ordinal))
            {
                m.Committed = true;
                if (StopAfterCommit) m.Cancelled = true;
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
        string root = Brain.VaultRoot;
        thread.FilesystemRoot = root;
        thread.IsBrainVault = true;
        thread.Ct = ct;

        ServerFileSystem fs = new(root, ct, brainVault: true);
        new Read(fs).Register(thread);
        new WriteFile(fs).Register(thread);
        new EditFile(fs).Register(thread);
        new MoveFile(fs).Register(thread);
        new DeleteFile(fs).Register(thread);
        new ListDirectory(fs).Register(thread);
        // search_files / find_files over the vault redirect to search_brain (alias-aware) — see ServerFileSystem.
        new SearchBrain().Register(thread);
        new SearchFiles(fs).Register(thread);
        new FindFiles(fs).Register(thread);

        // Git tools (and others, e.g. memory_tools' recall_memory/edit_memory) are deferred behind
        // request_tools(group) instead of always sitting in context, resolved generically via
        // ToolFactories (agent-agnostic — see Thread.FilesystemRoot). Any group named in an agent's
        // PreloadedTools (Agents.json) is loaded eagerly instead — for an agent that calls a deferred
        // group almost every turn, discovering it via list_tools/request_tools first is a wasted
        // round-trip (a full extra prefill+think cycle) on every single run.
        new ListTools(thread).Register(thread);
        new RequestTools(thread).Register(thread);
        foreach (string group in PreloadedTools ?? [])
            ToolFactories.LoadGroup(group, thread);

        new Neighbours().Register(thread);
        new MergeNotesTool().Register(thread);
    }

    // ── The walk ──────────────────────────────────────────────────────────────────────────
    protected enum EpochOutcome { Committed, NoChange, Stalled }

    // Classify how an epoch ended: a successful git commit ⇒ Committed; an explicit "no change" reply ⇒
    // NoChange (genuine no-op, counts toward convergence); anything else (an error, or the model thinking
    // itself into an empty turn without acting) ⇒ Stalled, which must NOT count as the graph being clean.
    // Virtual so a read-only walker (Curiosity) can treat "added a curiosity" as the productive outcome.
    protected virtual EpochOutcome AssessEpoch(Thread thread)
    {
        bool committed = thread.History.OfType<Response>()
            .SelectMany(r => r.Trace ?? Enumerable.Empty<TraceStep>())
            .Any(s => s.Kind == "tool_result" && s.Name == "git"
                      && (s.Text?.StartsWith("Committed", StringComparison.Ordinal) ?? false));   // only a commit returns this
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

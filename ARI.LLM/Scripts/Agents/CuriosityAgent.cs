using System.Text;
using System.Text.Json.Serialization;
using ARI.Brain;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

// Ari exploring her own memory for things she genuinely wonders about — the standalone successor to the old
// BrainScan, now a tool-driven graph WALK (like Refactor/Engram) rather than a folder loop. Each epoch it
// takes one high-degree node's neighbourhood, navigates the graph (read_file / neighbours / search), and
// records genuine open questions with add_curiosity. It never mutates the vault — read-only + curiosities.
// Runs every 6 hours while idle (Scheduler-gated).
internal sealed class CuriosityAgent : MemoryAgent
{
    [JsonIgnore] internal string PersistentDir { get; set; } = string.Empty;

    // Neighbourhoods explored per run. Curiosities are sparse, so we don't converge on quiet epochs
    // (convergeOnNoChange:false) — we cover a broad spread of the graph each run and stop at this cap.
    private const int CURIOSITY_EPOCHS = 20;

    private readonly SemaphoreSlim runLock = new(1, 1);

    // Background reflection — think as much as it needs, but stay quiet in the main log.
    internal override bool QuietLogging => true;

    public CuriosityAgent() { }

    internal async Task<string> Run(CancellationToken ct = default)
    {
        if (!await runLock.WaitAsync(TimeSpan.FromSeconds(5)))
            return "Curiosity skipped — already running.";
        try
        {
            Shared.Logger.LogInformation("[Curiosity] Starting curiosity walk (up to {Epochs} neighbourhoods).", CURIOSITY_EPOCHS);
            Thread parent = new(ThreadPipeline.Dialogue, $"curiosity:{Guid.NewGuid():N}") { Internal = true };
            return await RunWalk(parent, parent.Key, PromptText("Task", ""), PersistentDir, CURIOSITY_EPOCHS, ct, null,
                                 convergeOnNoChange: false);
        }
        catch (Exception ex)
        {
            Shared.Logger.LogError("[Curiosity] Failed: {Message}", ex.Message);
            return $"Curiosity failed: {ex.Message}";
        }
        finally { runLock.Release(); }
    }

    // No commit to stop on — an epoch explores a neighbourhood, records any curiosities, and ends naturally
    // (or at the work-call breaker). StopAfterCommit=false so nothing forces the turn to end early.
    protected override bool StopAfterCommit => false;

    // The tidy taxonomy rulebook is irrelevant here; Curiosity's persona/context lives in its SystemPrompt.
    internal override string BuildPersistentContext(Thread thread) => string.Empty;

    // Read-only navigation + curiosity tools. NO write/edit/move/delete/git — Curiosity never changes the
    // vault; it only reads and records questions.
    protected override void RegisterTools(Thread thread, string persistentDir, CancellationToken ct)
    {
        string root = BrainModule.VaultRoot;
        ServerFileSystem fs = new(root, ct, brainVault: true);
        new ReadFile(fs).Register(thread);
        new ListDirectory(fs).Register(thread);
        // search_files / find_files over the vault redirect to search_brain (alias-aware) — see ServerFileSystem.
        new SearchBrain().Register(thread);
        new SearchFiles(fs).Register(thread);
        new FindFiles(fs).Register(thread);
        new Neighbours().Register(thread);
        new AddCuriosity(persistentDir).Register(thread);
        new ListCuriosities(persistentDir).Register(thread);
    }

    // Productive when the epoch recorded at least one curiosity; otherwise it explored and found nothing
    // worth asking about (NoChange — does not stop the walk, since convergeOnNoChange is false). True errors
    // are caught upstream in RunWalk and counted as Stalled.
    protected override EpochOutcome AssessEpoch(Thread thread) =>
        EpochCalled(thread, "add_curiosity") ? EpochOutcome.Committed : EpochOutcome.NoChange;

    // Read-only guard: block re-reading a note already read this epoch (its content is in context), but do
    // NOT cap distinct reads the way the tidy walk does — exploring for curiosities legitimately reads more
    // notes. The work-call breaker (EPOCH_TOOL_CEILING) is the real bound on an epoch's length.
    protected override string? PreToolGuard(Thread thread, ToolTurnState state, string toolName, string callId, string argsJson)
    {
        if (state is not MemoryTurnState m || toolName != "read_file") return null;
        string? path = ArgPath(argsJson);
        if (path is not null && m.ReadPaths.Contains(path))
        {
            Shared.Logger.LogInformation("[Curiosity] read guard: blocked RE-READ of {Path}.", path);
            return $"[System: you already read {path} this epoch — its content is above. Don't re-read it; explore a " +
                   $"different note (read_file/neighbours) or record what you've found with add_curiosity.]";
        }
        return null;
    }

    // Curiosity wanders rather than tidies, so it carries its own epoch prompt; with no entry of its
    // own it falls back to the shared MemoryAgent one.
    protected override string BuildEpochPrompt(string task, string seedTitle, string skeleton)
    {
        if (Prompts is null || !Prompts.ContainsKey("EpochPrompt"))
            return base.BuildEpochPrompt(task, seedTitle, skeleton);

        return PromptText("EpochPrompt", "",
            ("seedTitle", seedTitle),
            ("skeleton",  skeleton.Length > 0 ? skeleton : "(no connections)"),
            ("task",      task));
    }

    // The exploration brief handed to each epoch alongside the neighbourhood skeleton. Persona lives in config.
}

using System.Text.Json.Serialization;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

// Refactor walks the whole graph to keep it TIDY (curiosity-hunting is the separate Curiosity agent's job).
// Each epoch it takes one high-degree node's neighbourhood, makes one logical change (route sprawl through
// hubs, drop redundant edges, promote bridges, dissolve thin hubs, type nodes, merge duplicates), reviews
// the diff and commits it, then moves to the next seed — until the graph converges or the epoch cap is hit.
internal sealed class Refactor : MemoryAgent
{
    [JsonIgnore] internal Engram? engram      { get; set; }
    [JsonIgnore] internal string  PersistentDir { get; set; } = string.Empty;

    private readonly SemaphoreSlim runLock = new(1, 1);

    // Incremental vs full only changes how many epochs the walk is allowed before stopping.
    private const int INCREMENTAL_EPOCHS = 12;

    public Refactor() { }

    // Refactor is tidy-only — curiosity-recording is the Curiosity agent's job now.
    protected override bool IncludeCuriosityTools => false;

    // Refactor rotates through the whole vault least-recently-refactored first (see RefactorLog); the
    // other memory walks keep the plain top-degree seed order.
    protected override bool TrackLastRefactored => true;

    internal async Task<string> Run(bool allNotes = false, CancellationToken ct = default, int? epochsOverride = null)
    {
        if (!await runLock.WaitAsync(TimeSpan.FromSeconds(5)))
            return "Refactor skipped — already running.";

        // Pause Engram for the duration: nothing should write to the graph while it is being restructured.
        bool engramWasEnabled = engram?.IsEnabled ?? false;
        if (engramWasEnabled)
        {
            engram!.Disable();
            Shared.Logger.LogInformation("[Refactor] Engram paused for refactor.");
        }

        try
        {
            int epochs = epochsOverride ?? (allNotes ? DEFAULT_MAX_EPOCHS : INCREMENTAL_EPOCHS);
            Shared.Logger.LogInformation("[Refactor] Starting graph walk ({Mode}, up to {Epochs} epochs).",
                allNotes ? "full" : "incremental", epochs);

            Thread parent = new(ThreadPipeline.Dialogue, $"refactor:{Guid.NewGuid():N}") { Internal = true };
            return await RunWalk(parent, parent.Key, PromptText("Task", ""), PersistentDir, epochs, ct, null);
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

    // The tidy contract handed to each epoch, on top of the seed's neighbourhood skeleton. The full
    // taxonomy/naming rulebook lives in this agent's SystemPrompt (config); this is the walk-specific job.
}

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
            return await RunWalk(parent, parent.Key, RefactorTask, PersistentDir, epochs, ct, null);
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
    private const string RefactorTask = """
        Your job is to tidy this region of the memory graph and note anything worth asking Xywren about.

        FIX THE SEED NOTE (the note this neighbourhood is centred on). Do not wander off to reorganise an
        unrelated note on a hunch — if the seed is over the degree cap, that IS the fix; do it and commit.
        Reduce outbound links by DELETING the specific `[[link]]` text with edit_file (target the exact
        lines, empty new_string to remove a whole line, or edit the line to drop just the link). NEVER use
        write_file to rewrite an existing note wholesale — it destroys the note's YAML frontmatter and body
        structure. write_file is ONLY for creating a brand-new note that does not exist yet.

        Look for ONE thing to fix here, in priority order:
        - OUTBOUND SPRAWL: a node (especially a person/root) linking directly to many unrelated leaves.
          Route it through hubs instead — links to hubs are "free", direct links to individual leaves are
          not. Group ≥3 themed leaves under an owned, namespaced hub (e.g. "[REDACT]'s Family", not "Family").
        - REDUNDANT EDGES: a direct A→B link where A already reaches B through a hub or bridge. Delete the
          direct edge; keep the routed path. (Inbound links are fine and cross-links that aid clustering are
          fine — only tame runaway OUTBOUND fan-out.)
        - BRIDGES: two people connected only by a bare link — prefer expressing the connection through an
          event (a bounded moment) or a relationship (the ongoing thread). Events hold the circumstances of
          a moment and never later developments; relationships hold everything that unfolds after.
        - THIN HUBS: dissolve ONLY an emergent/one-off theme hub with fewer than 3 members (move members
          up, delete the hub). Do NOT dissolve a standing category hub — Family, Friends, Employment,
          Education, Tech, Pets, Relationships are valid even with 1-2 members and will grow.
        - TYPES: set a node's type (hub/event/relationship/discussion/person, or a new one) when it's a
          distinct kind and lacks one.
        - DUPLICATES: two notes that are the same entity — merge_notes them.

        If nothing here needs changing, reply 'no change'.
        """;
}

using ARI.Common;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ARI.BrainVault;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Engram : MemoryAgent, IDisposable
{
    // ── Constants ────────────────────────────────────────────────────────────────
    private const int    EXTRACT_MAX_TOKENS   = 6000;
    private const double EXTRACT_TEMPERATURE = 0.3;
    private const int    SAVE_MAX_TOKENS      = 300;
    private const int    OVERSIZED_BLOCK_CHARS = 600;

    // Each Stage-3 call now places exactly one entity in its own short-lived thread, so there's no
    // multi-entity turn left to keep open — this and the ceiling below fall back to MemoryAgent's
    // defaults (single-commit-per-turn, 8-call breaker), which fit a one-note, one-tool call cleanly.
    // The old override existed because Engram used to recon a whole entity SET (find/search/read many
    // notes) before writing any of them inside one giant turn — that turn shape is gone.

    [JsonIgnore] internal Dialogue?    dialogue       { get; set; }
    [JsonIgnore] internal Context?     context        { get; set; }
    [JsonIgnore] internal string       PersistentDir  { get; set; } = string.Empty;

    // Completed exchanges (a message from the user and the reply) an active thread accumulates before an interval sweep; 0 disables it.
    public int TurnsBeforeSweep { get; set; } = 10;

    private readonly Dictionary<string, DateTime>       lastRun          = new();
    private readonly Dictionary<string, int>            lastHistoryCount = new();
    private readonly ConcurrentDictionary<string, int>  exchangeCounts   = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A thread restored from disk (Persistent Threads) already went through Engram: sweeps start after these items.</summary>
    internal void MarkHistoryRestored(string threadKey, int itemCount) { lock (lastRun) lastHistoryCount[threadKey] = itemCount; }
    private readonly SemaphoreSlim                      engramLock       = new(1, 1);
    private readonly SemaphoreSlim                      reflexLock       = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> sweepingThreads  = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>                    pendingQueue     = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient                         httpClient       = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    // Per-thread count of exchanges since the thread was last swept, for any reason. Reset when a sweep starts.
    internal bool ShouldIntervalSweep(string threadKey)
    {
        if (TurnsBeforeSweep <= 0) return false;
        int count = exchangeCounts.AddOrUpdate(threadKey, 1, (_, c) => c + 1);
        return count >= TurnsBeforeSweep;
    }

    // Sweeps (interval, compaction) that run beside the conversation: the reply does not wait for them.
    private readonly ConcurrentDictionary<string, byte> backgroundSweeps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A sweep the next reply must wait for (dormant, closed, manual...). Interval and compaction sweeps run in parallel with replies.</summary>
    internal bool BlocksReply(string threadKey) => IsSweeping(threadKey) && !backgroundSweeps.ContainsKey(threadKey);

    private ConcurrentDictionary<string, Thread> threads = new();

    internal event Action<string>? SweepCompleted;

    internal bool IsSweeping(string threadKey) => sweepingThreads.ContainsKey(threadKey);

    /// <summary>Saving something right now, from a full sweep or from the chat's quick "remember" saves. Drives the "adding to memory" notice.</summary>
    internal bool IsSaving(string threadKey) => IsSweeping(threadKey) || reflexDraining.ContainsKey(threadKey);

    internal async Task WaitForSweep(string threadKey, CancellationToken ct)
    {
        if (!IsSweeping(threadKey)) return;

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<string>? handler = null;
        handler = key =>
        {
            if (!key.Equals(threadKey, StringComparison.OrdinalIgnoreCase)) return;
            SweepCompleted -= handler;
            tcs.TrySetResult(true);
        };
        SweepCompleted += handler;
        if (!IsSweeping(threadKey)) { SweepCompleted -= handler; return; }

        using CancellationTokenRegistration reg = ct.Register(() => { SweepCompleted -= handler; tcs.TrySetCanceled(ct); });
        await tcs.Task;
    }

    public Engram() { }

    internal static Engram? Current { get; private set; }

    internal void Init(Dialogue dialogue, Context? context, ConcurrentDictionary<string, Thread> threads)
    {
        Current = this;
        this.dialogue = dialogue;
        this.context  = context;
        this.threads  = threads;

        // Engram now runs solely on a thread's transition to dormant (wired via Thread.BecameDormant in
        // LLMModule). The old inactivity buffer/drain is gone — it could be starved indefinitely by an
        // unanswered proactive thread sitting idle. Deletion cleanup only.
        dialogue.ThreadDeleted += threadKey =>
        {
            lastRun.Remove(threadKey);
            lastHistoryCount.Remove(threadKey);
            lock (pendingQueue) pendingQueue.Remove(threadKey);
        };
    }

    internal bool IsEnabled { get; private set; } = true;

    internal void Enable()
    {
        IsEnabled = true;
        Shared.Logger.LogInformation("[Engram] Enabled.");

        string[] queued;
        lock (pendingQueue)
        {
            queued = [.. pendingQueue];
            pendingQueue.Clear();
        }
        foreach (string key in queued)
            _ = Task.Run(async () =>
            {
                try { await RunEngram(key, "queued"); }
                catch (Exception ex) { Shared.Logger.LogWarning("[Engram] Queued sweep failed for {Key}: {Err}", key, ex.Message); }
            });
        if (queued.Length > 0)
            Shared.Logger.LogInformation("[Engram] Draining {Count} queued thread(s).", queued.Length);
    }

    internal void Disable()
    {
        IsEnabled = false;
        Shared.Logger.LogInformation("[Engram] Disabled.");
    }

    internal int PurgeNotes() => Brain.PurgeAllNotes();

    public void Dispose()
    {
        engramLock.Dispose();
        httpClient.Dispose();
    }

    /// <summary>Sweeps a thread into memory. <paramref name="force"/> (manual close) bypasses the disabled
    /// gate and waits for the sweep lock rather than skipping. On a completed run — including a "nothing to
    /// store" classification — the thread's <see cref="Thread.EngramProcessed"/> flag is set, which is what
    /// releases it for deletion. If the run can't start (disabled, or a concurrent sweep holds the lock) the
    /// flag is left untouched so the caller's delete-retry poll tries again.</summary>
    /// <summary>A thread Engram may remember: the Master's own conversations, and with the "remember other people" setting on, everyone else's too.</summary>
    internal static bool MaySweep(Thread thread) =>
        !thread.Internal && thread.HasUserMessages && (EngramOthersSetting.Enabled || (thread.HasMemoryAccess && thread.OwnerId is not null));

    /// <summary>Copy of a live history: a reply may append to it while a parallel sweep reads.</summary>
    private static List<ThreadItem> SnapshotHistory(Thread t)
    {
        for (int i = 0; i < 5; i++)
            try { return new List<ThreadItem>(t.History); } catch (InvalidOperationException) { }
        return new List<ThreadItem>();
    }

    internal async Task RunEngram(string threadKey, string trigger, bool force = false)
    {
        // In DevMode Engram still runs in full: the brain it writes to is a throwaway copy (see LLMModule.PrepareDevSandbox), so nothing it does is kept.

        // Guild threads don't reach the brain unless "remember other people" is switched on. A Discord server is multi-party: anyone whitelisted can
        // speak into the transcript, and a sweep treats every line as material to store — so a guest's
        // provocation becomes a durable note about a third party, and guarded mode cannot help because it
        // filters recall, not writes. Master-only filtering is not a substitute: the misfiled note that
        // prompted this gate was built from the Master's own messages, misread by pronoun resolution.
        // Force-proof like the DevMode gate above — a manual close must not be a way around it either.
        if (threadKey.StartsWith("guild:", StringComparison.Ordinal) && !EngramOthersSetting.Enabled)
        {
            if (threads.TryGetValue(threadKey, out Thread? guildThread)) guildThread.EngramProcessed = true;
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] skipped — guild threads are never swept into the brain.", threadKey);
            return;
        }

        // Same for a DM conversation ARI opened with someone other than the Master (discord_dm_user): what they say
        // is theirs, not material for the Master's brain. Keys are dm:{userId} or dm:{userId}:{opened}.
        if (!EngramOthersSetting.Enabled && threadKey.StartsWith("dm:", StringComparison.Ordinal) && Modules.Discord is { } discord
            && ulong.TryParse(threadKey.Split(':')[1], out ulong dmUser) && dmUser != discord.MasterDiscordId)
        {
            if (threads.TryGetValue(threadKey, out Thread? dmThread)) dmThread.EngramProcessed = true;
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] skipped — DMs with people other than the Master are never swept into the brain.", threadKey);
            return;
        }

        if (!IsEnabled && !force)
        {
            lock (pendingQueue) pendingQueue.Add(threadKey);
            return;
        }
        if (force) await engramLock.WaitAsync();
        else if (!await engramLock.WaitAsync(0)) return;
        sweepingThreads[threadKey] = 0;
        if (trigger is "interval" or "compaction") backgroundSweeps[threadKey] = 0;
        exchangeCounts[threadKey] = 0;

        // --- Session record: the sweep's non-LLM decisions. The placement thread's own LLM traffic is
        //     recorded by the agent itself; these are the facts around it that explain the run. ---
        string  placementKey = "";
        string  transcriptSeen = "";
        string  outcome        = "incomplete (unexpected exit)";
        int     committed      = 0;
        int     blockedCount   = 0;
        bool    processed      = false;

        try
        {
            List<ThreadItem> allItems = threads.TryGetValue(threadKey, out Thread? dialogueThread) ? SnapshotHistory(dialogueThread) : new List<ThreadItem>();
            List<ThreadItem> conversationItems = allItems.Where(i => i is LLM.Prompt or Response).ToList();

            // A code thread is swept with its own prompts: what was done and decided, filed on the project's note —
            // never the code itself (the repo and git already hold that).
            bool    code    = dialogueThread?.Pipeline == ThreadPipeline.Code;
            string? project = code ? ProjectNameFor(dialogueThread!) : null;

            int lastCount = lastHistoryCount.TryGetValue(threadKey, out int c) ? c : 0;
            List<ThreadItem> recentItems = conversationItems.Skip(lastCount).ToList();

            lastRun[threadKey]          = DateTime.UtcNow;
            lastHistoryCount[threadKey] = conversationItems.Count;

            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] sweep triggered (trigger: {Trigger})", threadKey, trigger);

            // No user messages — ARI-only thread (proactive, internal monologue, etc.). Nothing to
            // store: the user said nothing and there is no interaction to remember.
            if (!conversationItems.OfType<Prompt>().Any())
            {
                outcome   = "skipped — no user messages (ARI-only thread)";
                processed = true;
                return;
            }

            // --- Classify: is there anything worth remembering? ---
            transcriptSeen = BuildTranscript(recentItems, code);
            if (string.IsNullOrWhiteSpace(transcriptSeen))
            {
                outcome   = "skipped — no new messages";
                processed = true;
                return;
            }

            // recentItems is only what's new since lastHistoryCount — a 2nd+ sweep never re-reads the full transcript.
            string transcript = transcriptSeen.Length > 0 ? transcriptSeen : BuildTranscript(recentItems, code);
            if (context is not null) await context.RebuildFromTranscript(threadKey, transcript);
            string contextSummary = context?.GetContext(threadKey) ?? string.Empty;
            string speaker = conversationItems.OfType<Prompt>().Select(p => p.AuthorName)
                .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
                ?? ReadStoredUserName()
                ?? "the user";

            // --- Stage 2: extract everything worth storing, regardless of whether it's already known —
            //     Stage 3 (not this step) is what checks the vault and decides new/duplicate/updated. One
            //     tool-free call, so it can't wander; the excerpt it captures per entity is what keeps
            //     Stage 3 faithful to what was actually said without needing one continuous thread. ---
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] extracting entities...", threadKey);
            List<ExtractedEntity> entities = await ExtractAsync(transcript, contextSummary, code, project);

            // The conversation's own date — not "today", since Engram can run well after the fact.
            // Stage 3 is told this explicitly so it never has to guess or invent one.
            DateTime logStart = recentItems.Count > 0 ? recentItems[0].Timestamp : DateTime.Now;
            DateTime logEnd   = recentItems.Count > 0 ? recentItems[^1].Timestamp : DateTime.Now;
            string   conversationDate = logStart.ToString("yyyy-MM-dd");

            // --- Stage 3: save everything from this conversation in one call. Each entity's note is
            //     still resolved by exact match beforehand and the write tool is still confined to that
            //     pre-resolved set of paths — one call now covers the whole sweep's entities instead of
            //     one call per entity, but no entity can still reach a note it wasn't resolved against. ---
            (int commits, int blocked) = await SaveAllAsync(threadKey, entities, speaker, conversationDate, code, project);
            if (!code && entities.Count > 0) CleanLinks();

            // A short linked summary of what was concluded; falls back to a linked entity list if the summary call fails.
            if (entities.Count > 0)
            {
                string logLine = code
                    ? await GenerateCodeSummary(transcript, speaker, project)
                    : await GenerateConversationSummary(transcript, speaker);
                if (string.IsNullOrWhiteSpace(logLine))
                {
                    IEnumerable<string> linked = entities.Select(e => e.Entity).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(e => !e.Equals(speaker, StringComparison.OrdinalIgnoreCase))
                        .Select(e => $"[[{e}]]");
                    logLine = "Discussed: " + string.Join(", ", linked) + ".";
                }
                if (code)
                    AppendToConversationLog(logStart, logLine, logStart, logEnd, "Coding session");
                else
                    AppendToConversationLog(logStart, logLine, logStart, logEnd);
            }

            committed    = commits;
            blockedCount = blocked;
            outcome      = entities.Count == 0
                ? "0 memory change(s) committed (nothing extracted)"
                : $"{commits} memory change(s) committed ({entities.Count} entit{(entities.Count == 1 ? "y" : "ies")} considered" +
                  (blocked > 0 ? $", {blocked} blocked by the write-scope guard" : "") + ")";
            processed = true;
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] sweep complete — {Commits}/{Total} change(s), {Blocked} blocked.", threadKey, commits, entities.Count, blocked);
        }
        finally
        {
            SessionRecorder.StandaloneNote("Engram", placementKey.Length > 0 ? placementKey : $"engram:{threadKey}", "sweep", new Dictionary<string, object?>
            {
                ["trigger"]             = trigger,
                ["swept_thread"]        = threadKey,
                ["classified_transcript"] = transcriptSeen,
                ["commits"]             = committed,
                ["blocked"]             = blockedCount,
                ["outcome"]             = outcome,
                ["processed"]           = processed,
            });

            // The invariant latch: a completed sweep (or a "nothing to store") releases the thread for deletion.
            if (processed && threads.TryGetValue(threadKey, out Thread? processedThread))
                processedThread.EngramProcessed = true;

            sweepingThreads.TryRemove(threadKey, out _);
            backgroundSweeps.TryRemove(threadKey, out _);
            engramLock.Release();
            SweepCompleted?.Invoke(threadKey);
        }
    }

    // Test-only: runs Extract like RunEngram, then stops — never touches Save or writes anything.
    internal async Task<List<(string Entity, bool IsNew, string Excerpt, bool Sensitive)>> DebugExtractOnly(string threadKey)
    {
        List<ThreadItem> allItems = threads.TryGetValue(threadKey, out Thread? dialogueThread) ? SnapshotHistory(dialogueThread) : new List<ThreadItem>();
        List<ThreadItem> conversationItems = allItems.Where(i => i is LLM.Prompt or Response).ToList();
        List<ThreadItem> recentItems = conversationItems;   // treat as a fresh, first-ever sweep

        string transcript = BuildTranscript(recentItems);
        string contextSummary = context?.GetContext(threadKey) ?? string.Empty;
        List<ExtractedEntity> entities = await ExtractAsync(transcript, contextSummary);
        return entities.Select(e => (e.Entity, e.IsNew, e.Excerpt, e.Sensitive)).ToList();
    }

    // ── Stage 2: extract ───────────────────────────────────────────────────────────────

    // Entity is always the plain subject name, never an invented compound title — that's what let two notes about the same subject drift apart and never resolve to each other.
    private readonly record struct ExtractedEntity(string Entity, bool IsNew, string Excerpt, bool Sensitive,
        string Kind = "entity", string Theme = "", string Involves = "", string Date = "", bool Correction = false, string Thought = "", int Retries = 0);

    /// <summary>One tool-free completion over the whole transcript: every entity worth remembering,
    /// regardless of whether it's already in the vault (Stage 3 checks that), each with the actual
    /// transcript lines it came from — not a paraphrase, so Stage 3 has real material to work from
    /// without needing to share a thread with this call.</summary>
    // A small model told to list everything in a long message summarises instead. Reading it a few paragraphs at a time
    // keeps each answer short and complete, so nothing in the middle is skipped.
    private const int EXTRACT_CHUNK_CHARS = 1600;

    private async Task<List<ExtractedEntity>> ExtractAsync(string transcript, string contextSummary, bool code = false, string? project = null)
    {
        if (code || transcript.Length <= EXTRACT_CHUNK_CHARS) return await ExtractChunkAsync(transcript, contextSummary, code, project);

        List<ExtractedEntity> all = new();
        StringBuilder chunk = new();
        async Task Flush()
        {
            if (chunk.ToString().Trim().Length == 0) return;
            all.AddRange(await ExtractChunkAsync(chunk.ToString(), contextSummary, code, project));
            chunk.Clear();
        }
        // A chunk that starts mid-message would lose who is speaking, and the model then takes the most-named person for the speaker.
        string label = "";
        foreach (string paragraph in transcript.Split("\n\n"))
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(paragraph, @"^([^\n:]{1,40}): ");
            if (m.Success) label = m.Groups[1].Value;
            foreach (System.Text.RegularExpressions.Match inner in System.Text.RegularExpressions.Regex.Matches(paragraph, @"^([A-Za-z][^\n:]{0,39}): ", System.Text.RegularExpressions.RegexOptions.Multiline))
                label = inner.Groups[1].Value;
            if (chunk.Length > 0 && chunk.Length + paragraph.Length > EXTRACT_CHUNK_CHARS) await Flush();
            if (chunk.Length == 0 && !m.Success && label.Length > 0) chunk.Append(label).Append(" (continued): ");
            chunk.Append(paragraph).Append("\n\n");
        }
        await Flush();
        return all;
    }

    private async Task<List<ExtractedEntity>> ExtractChunkAsync(string transcript, string contextSummary, bool code = false, string? project = null)
    {
        string context = string.IsNullOrWhiteSpace(contextSummary)
            ? ""
            : ResolveTemplate("ContextBlock", "", ("contextSummary", contextSummary));

        object requestBody = new
        {
            model    = "local",
            messages = new[]
            {
                new { role = "system", content = (code
                        ? ResolveTemplate("CodeExtractSystem", CodePrompts.ExtractSystem, ("projectNote", ProjectNoteName(project)))
                        : ResolveTemplate("ExtractSystem", "")) + "\n<|think_off|>" },
                new { role = "user",   content = code
                        ? ResolveTemplate("CodeExtractTask", CodePrompts.ExtractTask, ("context", context), ("transcript", transcript))
                        : ResolveTemplate("ExtractTask", "", ("context", context), ("transcript", transcript)) }
            },
            stream      = false,
            max_tokens  = EXTRACT_MAX_TOKENS,
            temperature = EXTRACT_TEMPERATURE,
            thinking             = false,
            enable_thinking      = false,
            chat_template_kwargs = new { enable_thinking = false }
        };

        try
        {
            HttpRequestMessage request = new(HttpMethod.Post, $"{Endpoint}/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            HttpResponseMessage response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);
            string content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return ParseExtracted(content);
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[Engram] Extraction failed ({Error}) — treating as nothing extracted.", ex.Message);
            return new();
        }
    }

    // The model may wrap the array in prose or a fenced code block despite instructions — pull out the
    // first [...] span rather than requiring the whole completion to be pure JSON. If the answer was cut off
    // (token cap) or is otherwise not valid JSON, keep every object that did complete instead of losing all of them.
    private static List<ExtractedEntity> ParseExtracted(string content)
    {
        int start = content.IndexOf('[');
        int end   = content.LastIndexOf(']');
        if (start < 0) return new();

        if (end > start)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(content[start..(end + 1)]);
                return doc.RootElement.EnumerateArray().Select(ToEntity).OfType<ExtractedEntity>().ToList();
            }
            catch (JsonException ex)
            {
                Shared.Logger.LogWarning("[Engram] Extraction output isn't valid JSON ({Error}) — salvaging the complete entries.", ex.Message);
            }
        }

        List<ExtractedEntity> salvaged = new();
        foreach (string obj in JsonObjectSalvage.CompleteObjects(content))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(obj);
                if (ToEntity(doc.RootElement) is { } entity) salvaged.Add(entity);
            }
            catch (JsonException) { /* a malformed entry — skip just that one */ }
        }
        Shared.Logger.LogInformation("[Engram] Salvaged {Count} entit{Suffix} from a truncated extraction.", salvaged.Count, salvaged.Count == 1 ? "y" : "ies");
        return salvaged;
    }

    private static ExtractedEntity? ToEntity(JsonElement el)
    {
        string entity    = el.TryGetProperty("entity",    out JsonElement e) ? e.GetString() ?? "" : "";
        string excerpt   = el.TryGetProperty("excerpt",   out JsonElement x) ? x.GetString() ?? "" : "";
        bool   isNew     = el.TryGetProperty("is_new",    out JsonElement n) && n.ValueKind == JsonValueKind.True;
        bool   sensitive = el.TryGetProperty("sensitive", out JsonElement s) && s.ValueKind == JsonValueKind.True;
        string Text(string prop) => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string involves = el.TryGetProperty("involves", out JsonElement inv) && inv.ValueKind == JsonValueKind.Array
            ? string.Join(", ", inv.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()))
            : Text("involves");
        bool correction = el.TryGetProperty("correction", out JsonElement cor) && cor.ValueKind == JsonValueKind.True;
        string kind = Text("kind").ToLowerInvariant() is "event" or "relationship" ? Text("kind").ToLowerInvariant() : "entity";
        return entity.Length > 0 && excerpt.Length > 0 ? new ExtractedEntity(entity, isNew, excerpt, sensitive, kind, Text("theme"), involves, Text("date"), correction, Text("thought")) : null;
    }

    // ── Stage 3: save ──────────────────────────────────────────────────────────────────

    // One resolved entity ready for the Save call: its allowed write name(s)/prefix and the
    // existing-content block describing what (if anything) is already there.
    private readonly record struct ResolvedEntity(string Entity, string ExistingBlock, HashSet<string> AllowedNames, string AllowedPrefix);

    // Resolves one entity's note by exact match, never fuzzy search — a fuzzy match once let a write land on an unrelated note.
    private static ResolvedEntity ResolveEntity(ExtractedEntity entity)
    {
        Note? note = Brain.GetNote(entity.Entity);
        string block = note is not null
            ? $"A note with this name exists: '{note.Name}'. Read it (recall_memory) before deciding where this belongs."
            : "No note has exactly this name. Search (search_brain) in case it's already known under another name, or is a detail of a note that already exists.";
        if (entity.Sensitive)
            block += "\nThis is private. Keep it with everything else about its subject, in its own short `> [!sensitive]` paragraph covering only that fact.";
        return new ResolvedEntity(entity.Entity, block, new HashSet<string>(StringComparer.OrdinalIgnoreCase), note?.Name ?? "");
    }

    // Saves every entity in one call; edit_memory is confined to each entity's own pre-resolved name/prefix (see ResolveEntity).
    private Thread EngramThread(string threadKey)
    {
        Thread Make(Thread? parent, string key)
        {
            Thread t = new(ThreadPipeline.Dialogue, key)
            {
                Internal = true, KeepToolHistory = true, Parent = parent, Code = parent is null ? null : "engram", OwnerAgent = Name, Label = "Engram",
            };
            t.FilesystemRoot = Brain.VaultRoot;
            t.IsBrainVault   = true;
            t.Ct             = CancellationToken.None;
            // Look before writing: find, read and follow links; then add to a note, make a new one, correct, delete, merge or relink. Each write commits itself.
            new SearchBrain().Register(t);
            new RecallMemory().Register(t);
            new Neighbours().Register(t);
            new MemoriesOn().Register(t);
            new CreateMemory().Register(t);
            new EditMemory().Register(t);
            new DeleteMemory().Register(t);
            new MergeNotesTool().Register(t);
            return t;
        }
        Thread? chat = threads.GetValueOrDefault(threadKey);
        return chat is null ? Make(null, $"engram:{threadKey}:{Guid.NewGuid():N}") : chat.GetOrCreateChild("engram", Name, () => Make(chat, $"{chat.Key}:engram"));
    }

    private async Task<(int Commits, int Blocked)> SaveAllAsync(string threadKey, List<ExtractedEntity> entities, string speaker, string conversationDate, bool code = false, string? project = null, bool reflex = false)
    {
        if (entities.Count == 0) return (0, 0);

        Brain.Index();   // pick up any commits made just before this sweep started
        List<ResolvedEntity> resolved = entities.Select(ResolveEntity).ToList();

        StringBuilder   entityBlocks    = new();
        // Entities resolving to the same note share one block, so the note gets one combined edit and one commit.
        IEnumerable<IGrouping<string, int>> byNote = Enumerable.Range(0, resolved.Count)
            .GroupBy(i => resolved[i].AllowedPrefix.Length > 0 ? resolved[i].AllowedPrefix : resolved[i].Entity, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, int> group in byNote)
        {
            // Prefer a sensitive member's block so its marking instruction isn't lost in the merge.
            ResolvedEntity r = resolved[group.OrderByDescending(i => entities[i].Sensitive).First()];
            string excerpts = string.Join("\n\n", group.Select(i => entities[i].Excerpt).Distinct());
            string header   = string.Join(" / ", group.Select(i => entities[i].Entity).Distinct(StringComparer.OrdinalIgnoreCase));
            ExtractedEntity lead = entities[group.OrderByDescending(i => entities[i].Correction).First()];
            string meta = $"Kind: {lead.Kind}" + (lead.Theme.Length > 0 ? $" · Theme: {lead.Theme}" : "") + (lead.Involves.Length > 0 ? $" · Involves: {lead.Involves}" : "")
                        + (lead.Date.Length > 0 ? $" · Date: {lead.Date}" : "");
            string note = lead.Correction ? "\nCORRECTION: the speaker said the older version of this was wrong or out of date. Replace it; do not keep both." : "";
            string thought = string.Join(" ", group.Select(i => entities[i].Thought).Where(t => t.Length > 0));
            if (thought.Length > 0) note += $"\nMARGIN THOUGHT to leave beside the fact it relates to (as a thought, never as a fact): {thought}";
            string groupHint = "";
            entityBlocks.Append($"### {header}\n{meta}{note}{groupHint}\n{r.ExistingBlock}\n\nWhat was said (speaker: {speaker}):\n{excerpts}\n\n");
        }

        // One Engram thread per conversation, kept under the chat like Recall's: it remembers what it read and decided across sweeps and
        // reflexes, instead of a new thread being born for every save. Only one run uses it at a time.
        Thread mini = EngramThread(threadKey);
        await mini.RunLock.WaitAsync();
        try
        {
        mini.EngramReflex = reflex;
        int historyBefore = mini.History.Count;

        string task = code
            ? ResolveTemplate("CodeSaveTask", CodePrompts.SaveTask,
                ("entities",    entityBlocks.ToString().TrimEnd()),
                ("date",        conversationDate),
                ("projectNote", ProjectNoteName(project)))
            : ResolveTemplate("SaveTask", "",
                ("entities", entityBlocks.ToString().TrimEnd()),
                ("date",     conversationDate));
        // A quick save leads with the conversation and treats the noted facts as the headline of what to change; a sweep keeps them first and adds the source after.
        if (!code) task = reflex
            ? RecentConversation(threadKey, true) + "\n\nHEADLINE: the chat noted these facts as what changed in the conversation above. Change only this, writing from the conversation, not from the headline's wording.\n\n" + task
            : task + RecentConversation(threadKey, false);

        await Prompt(mini, task, new PromptOptions
        {
            Username = "system",
            OnDelta  = async _ => { Notify?.Invoke(mini.Key); await Task.CompletedTask; },
        });

        // Pairs each tool_call to its next same-name tool_result so one turn covering several entities still reports per-note outcomes.
        List<TraceStep> trace = mini.History.Skip(historyBefore).OfType<Response>().SelectMany(r => r.Trace ?? Enumerable.Empty<TraceStep>()).ToList();
        int commits = 0, blocked = 0;
        for (int i = 0; i < trace.Count; i++)
        {
            if (trace[i].Kind != "tool_call" || trace[i].Name is not ("create_memory" or "edit_memory")) continue;
            string toolName = trace[i].Name!;
            string? name = null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(trace[i].Args ?? "{}");
                name = doc.RootElement.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
            }
            catch { }

            TraceStep? result = trace.Skip(i + 1).FirstOrDefault(s => s.Kind == "tool_result" && s.Name == toolName);
            bool wasBlocked = result?.Text?.StartsWith("[Blocked]", StringComparison.Ordinal) ?? false;
            if (wasBlocked)
            {
                blocked++;
                Shared.Logger.LogWarning("[Engram] [{ThreadKey}] {Tool} blocked for '{Name}' — scope guard rejected it.", threadKey, toolName, name ?? "(unknown)");
            }
            else
            {
                commits++;
            }
        }

        return (commits, blocked);
        }
        finally { mini.EngramReflex = false; mini.RunLock.Release(); }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────

    private static string NoMd(string path) => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;

    /// <summary>After a sweep: any link that points at nothing becomes plain words, so the graph only holds links someone can follow.</summary>
    private static void CleanLinks()
    {
        try
        {
            if (Brain.StripUnresolvedLinks() > 0) { AriGit.Run(Brain.VaultRoot, ["add", "-A"]); AriGit.Commit(Brain.VaultRoot, "Turn links to notes that don't exist into plain words"); }
        }
        catch (Exception ex) { Shared.Logger.LogWarning("[Engram] link cleanup failed: {Error}", ex.Message); }
    }

    // ── Reflex: one fact, one note, right now ───────────────────────────────────────────

    /// <summary>The quick path: a correction, or "remember this". It changes just that one note, without reading a conversation. Runs
    /// alongside the reply, so the chat never waits for it.</summary>
    internal override string OnToolResult(Thread thread, ToolTurnState state, string toolName, string argsJson, string result)
    {
        string text = base.OnToolResult(thread, state, toolName, argsJson, result);
        if (toolName == "recall_memory")
        {
            try { if (System.Text.Json.JsonDocument.Parse(argsJson).RootElement.TryGetProperty("name", out System.Text.Json.JsonElement rn) && rn.GetString() is { Length: > 0 } read) thread.BrainReads.Add(read); } catch { }
        }
        return text;
    }

    // Reads and edits as it goes: far more than the walk's eight calls before the breaker.
    internal override int? EpochToolCeiling => 40;

    private readonly ConcurrentDictionary<string, List<ExtractedEntity>> reflexQueue = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> reflexDraining = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Several facts said in one breath ("remember this" six times for one message) are collected for a moment and saved in one pass,
    /// so Engram sees them all together instead of queueing a pass for each.</summary>
    internal Task Remember(string threadKey, string about, string fact, bool correction)
    {
        if (!IsEnabled) return Task.CompletedTask;
        ExtractedEntity entity = new(about.Trim().Equals("Ari", StringComparison.OrdinalIgnoreCase) ? "ARI" : about.Trim(), false, fact.Trim(), false, Correction: correction);
        List<ExtractedEntity> queue = reflexQueue.GetOrAdd(threadKey, _ => new());
        lock (queue) queue.Add(entity);
        if (reflexDraining.TryAdd(threadKey, 0)) _ = Task.Run(() => DrainReflex(threadKey, queue));
        return Task.CompletedTask;
    }

    private async Task DrainReflex(string threadKey, List<ExtractedEntity> queue)
    {
        try
        {
            while (true)
            {
                await Task.Delay(2500);   // let the rest of a burst arrive
                List<ExtractedEntity> batch;
                lock (queue) { batch = queue.ToList(); queue.Clear(); }
                if (batch.Count == 0) break;
                await reflexLock.WaitAsync();
                try
                {
                    Shared.Logger.LogInformation("[Engram] [{ThreadKey}] reflex: {Count} fact(s) about {About}", threadKey, batch.Count, string.Join(", ", batch.Select(b => b.Entity).Distinct()));
                    await SaveAllAsync(threadKey, batch, SpeakerOf(threadKey), DateTime.Now.ToString("yyyy-MM-dd"), reflex: true);
                }
                catch (Exception ex)
                {
                    // The model server can fail for a moment (out of memory, restarting). Keep the facts and try again rather than lose them.
                    Shared.Logger.LogWarning("[Engram] reflex failed ({Error}); will retry.", ex.Message);
                    bool giveUp = batch.All(b => b.Retries >= 3);
                    if (!giveUp) { lock (queue) queue.AddRange(batch.Select(b => b with { Retries = b.Retries + 1 })); await Task.Delay(20000); }
                    else Shared.Logger.LogError("[Engram] gave up saving {Count} fact(s) after retries: {About}", batch.Count, string.Join(", ", batch.Select(b => b.Entity)));
                }
                finally { reflexLock.Release(); }
            }
        }
        finally { reflexDraining.TryRemove(threadKey, out _); lock (queue) { if (queue.Count > 0 && reflexDraining.TryAdd(threadKey, 0)) _ = Task.Run(() => DrainReflex(threadKey, queue)); } }
    }

    private const int SOURCE_ITEMS = 10, SOURCE_CHARS = 9000;

    /// <summary>The last few turns, word for word. The facts above are someone's summary of them (the chat's notes, or the extraction), and a summary can
    /// be wrong, so Engram checks every fact against what was actually said and writes from that.</summary>
    private string RecentConversation(string threadKey, bool leading)
    {
        if (threads.GetValueOrDefault(threadKey) is not { } chat) return "";
        List<ThreadItem> recent = chat.History.Where(i => i is LLM.Prompt or Response).TakeLast(SOURCE_ITEMS).ToList();
        string text = BuildTranscript(recent);
        if (text.Length > SOURCE_CHARS) text = text[^SOURCE_CHARS..];
        return text.Trim().Length == 0 ? "" :
            (leading ? "" : "\n\n") + "THE CONVERSATION ITSELF, word for word (the most recent turns). Any facts noted for you were written in someone else's words and may be wrong or coloured; this is the source of truth. "
          + "Check every fact against it, write from what the person actually said (their words and facts, never ARI's phrasing or opinions), and where the two disagree, trust this:\n"
          + text;
    }

    private string SpeakerOf(string threadKey) =>
        threads.GetValueOrDefault(threadKey)?.History.OfType<LLM.Prompt>().Select(p => p.AuthorName).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? ReadStoredUserName() ?? "the user";

    private static readonly HashSet<string> SelfWords = new(StringComparer.OrdinalIgnoreCase) { "master", "the master", "user", "the user", "me", "myself", "i", "owner", "the owner", "my" };
    private static readonly HashSet<string> RoleWords = new(StringComparer.OrdinalIgnoreCase)
        { "partner", "boyfriend", "girlfriend", "brother", "sister", "mum", "mom", "dad", "mother", "father", "friend", "wife", "husband", "boss", "colleague", "manager", "son", "daughter", "parents", "family" };

    /// <summary>What a reflex save is about: the person's own name for "me/Master/the user", the name as given otherwise. A role instead of a name
    /// ("partner"), or several things at once, comes back as guidance for the model to fix and call again.</summary>
    internal (string? Name, string? Error) ResolveAbout(string threadKey, string about)
    {
        string a = about.Trim();
        if (SelfWords.Contains(a)) return (SpeakerOf(threadKey), null);
        if (RoleWords.Contains(a) || RoleWords.Contains(a.TrimStart("my ".ToCharArray()).Trim()))
            return (null, "Use their actual name. If you don't know it, record this as a fact about the user instead (about: the user's own name).");
        if (a.Contains(" and ", StringComparison.OrdinalIgnoreCase) || a.Contains(',') || a.Contains('&') || a.Contains('/'))
            return (null, "One person, place or thing per call. Call again for each of them.");
        return (a, null);
    }

    // ── Closing ARI: finish the conversations that haven't been swept yet ───────────────

    internal async Task FlushAsync(TimeSpan limit)
    {
        if (!IsEnabled) return;
        using CancellationTokenSource cts = new(limit);
        foreach (Thread thread in threads.Values.ToList())
        {
            if (cts.IsCancellationRequested) break;
            if (thread.EngramProcessed || !MaySweep(thread)) continue;
            try { await RunEngram(thread.Key, "shutdown", force: true).WaitAsync(cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Shared.Logger.LogWarning("[Engram] shutdown sweep failed for {Key}: {Error}", thread.Key, ex.Message); }
        }
    }

    // ── Code threads ───────────────────────────────────────────────────────────────────

    /// <summary>The brain note a code thread's decisions and status are filed on: <c>Projects/{name}</c>, the note
    /// the project service creates when a project is made. Empty when the thread has no project.</summary>
    private static string ProjectNoteName(string? project) => string.IsNullOrWhiteSpace(project) ? "" : $"Projects/{project.Trim()}";

    /// <summary>The project a code thread is working on — the bound project's name, else its folder's name.</summary>
    private static string? ProjectNameFor(Thread thread)
    {
        if (!string.IsNullOrWhiteSpace(thread.ProjectName)) return thread.ProjectName.Trim();
        string? folder = thread.FilesystemRoot is { } root ? Path.GetFileName(root.TrimEnd('/', '\\')) : null;
        return string.IsNullOrWhiteSpace(folder) ? null : folder;
    }

    private Task<string> GenerateCodeSummary(string transcript, string speaker, string? project) =>
        CompleteSummary(
            ResolveTemplate("CodeLogSummarySystem", CodePrompts.LogSummarySystem, ("project", string.IsNullOrWhiteSpace(project) ? "the project" : $"[[{project.Trim()}]]")),
            $"Summarise this coding session by {speaker}:\n\n{transcript}");

    private Task<string> GenerateConversationSummary(string transcript, string speaker) =>
        CompleteSummary(
            ResolveTemplate("LogSummarySystem",
                "You write one entry in ARI's log for the day. Say what was discussed: every topic, with the specifics that matter (names, numbers, dates, decisions, plans, how the person seemed to feel), in short past-tense sentences or a few bullets. Third person, refer to the user by name. It is a summary of the information, never a script, so no 'then she said' and no quotes. Wrap every named person, place, project or organisation in [[wikilinks]]. Put anything private (health, therapy, sexual or secret matters, anything they asked to keep private) in its own line starting '> [!sensitive]' so it can be left out when needed. Never mention the assistant. No preamble."),
            ResolveTemplate("LogSummaryTask", "Summarise this conversation with {speaker}:\n\n{transcript}",
                ("speaker", speaker), ("transcript", transcript)));

    private async Task<string> CompleteSummary(string system, string user)
    {
        object requestBody = new
        {
            model    = "local",
            messages = new[]
            {
                new { role = "system", content = system + "\n<|think_off|>" },
                new { role = "user",   content = user }
            },
            stream      = false,
            max_tokens  = SAVE_MAX_TOKENS,
            temperature = EXTRACT_TEMPERATURE,
            thinking             = false,
            enable_thinking      = false,
            chat_template_kwargs = new { enable_thinking = false }
        };

        try
        {
            HttpRequestMessage request = new(HttpMethod.Post, $"{Endpoint}/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            HttpResponseMessage response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[Engram] Summary generation failed: {Err}", ex.Message);
            return "";
        }
    }

    // Each entry carries the actual time range of that conversation (first message → last message),
    // rather than one undifferentiated blob per day — several separate conversations on the same day
    // stay distinguishable.
    private static void AppendToConversationLog(DateTime day, string summary, DateTime start, DateTime end, string label = "Conversation")
    {
        string noteName = Brain.DailyNoteName(day);
        string relativePath = Brain.PathFor(noteName);
        string path = Path.Combine(Brain.VaultRoot, relativePath);
        string existing = File.Exists(path) ? File.ReadAllText(path) : "";

        string timeRange = start.ToString("HH:mm") == end.ToString("HH:mm")
            ? start.ToString("HH:mm")
            : $"{start:HH:mm}–{end:HH:mm}";
        string entry = $"\n\n---\n### {timeRange} — {label}\n{summary}";
        List<string> touched = [relativePath];
        if (existing.Length > 0)
        {
            File.AppendAllText(path, entry);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            string title = Path.GetFileNameWithoutExtension(path);
            string monthNote = Path.GetDirectoryName(noteName)!.Replace('\\', '/');
            File.WriteAllText(path, $"---\ncreated: {stamp}\nupdated: {stamp}\ntype: discussion\n---\n# {title}\n\n**Parent:** [[{monthNote}]]{entry}");
            touched.AddRange(EnsureConversationHubs(day, stamp));
        }
        Brain.Index();
        GitCommitBrain($"Conversation log: {label.ToLowerInvariant()} — {day:yyyy-MM-dd}", touched);
    }

    // The year and month hubs a daily note hangs from; created on first use so the note is never orphaned.
    private static IEnumerable<string> EnsureConversationHubs(DateTime day, string stamp)
    {
        string yearNote  = $"Conversations/{day.Year}";
        string monthNote = Path.GetDirectoryName(Brain.DailyNoteName(day))!.Replace('\\', '/');
        string monthName = day.ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture);
        (string note, string parent, string heading, string blurb)[] hubs =
        [
            (yearNote,  "Conversations", day.Year.ToString(),           $"{day.Year} conversations, by month."),
            (monthNote, yearNote,        $"{monthName} {day.Year}",     $"{monthName} {day.Year} conversations."),
        ];
        foreach ((string note, string parent, string heading, string blurb) in hubs)
        {
            string relative = Brain.PathFor(note);
            string full = Path.Combine(Brain.VaultRoot, relative);
            if (File.Exists(full)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, $"---\ntype: hub\ncreated: {stamp}\nupdated: {stamp}\n---\n# {heading}\n\n**Parent:** [[{parent}]]\n\n{blurb}\n\n## Directory\n\n## Members\n");
            yield return relative;
        }
    }

    // relativePath scopes the commit to exactly the touched file — `git add -A` would sweep in unrelated dirty state.
    private static void GitCommitBrain(string message, IEnumerable<string> relativePaths)
    {
        try
        {
            string vault = Brain.VaultRoot;
            AriGit.Run(vault, ["add", "--", .. relativePaths]);
            AriGit.Commit(vault, message);
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[Engram] Git commit for code summary failed: {Err}", ex.Message);
        }
    }

    // ── Classify (unchanged) ─────────────────────────────────────────────────────────

    private static string? ReadStoredUserName()
    {
        try
        {
            string path = Path.Combine(Paths.PersistentData, "username.txt");
            if (!File.Exists(path)) return null;
            string name = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch { return null; }
    }

    private const int CODE_TURN_MAX_CHARS = 2500;

    // Code threads are full of fenced code, diffs and long plan payloads. Engram records what was done and decided,
    // never the code itself, so the model is shown the prose only: fences collapse to a marker and each turn is capped.
    private static readonly System.Text.RegularExpressions.Regex CodeFence =
        new("```.*?```", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string ForCodeTranscript(string text)
    {
        string prose = CodeFence.Replace(text, "[code omitted]").Trim();
        return prose.Length <= CODE_TURN_MAX_CHARS ? prose : prose[..CODE_TURN_MAX_CHARS] + " […]";
    }

    private static string BuildTranscript(IEnumerable<ThreadItem> items, bool code = false)
    {
        StringBuilder sb = new();
        foreach (ThreadItem item in items)
        {
            switch (item)
            {
                case Prompt u:
                    // A pasted or attached text file is part of what the person said: without it a message with only an attachment is empty.
                    string attached = string.Join("\n\n", (u.Attachments ?? new()).Where(a => !a.IsImage && !string.IsNullOrWhiteSpace(a.Content)).Select(a => a.Content.Trim()));
                    string said = string.IsNullOrWhiteSpace(attached) ? u.Text : $"{attached}\n\n{u.Text}".Trim();
                    sb.AppendLine($"{u.AuthorName}: {(code ? ForCodeTranscript(said) : said)}");
                    break;
                case Response r: sb.AppendLine($"ARI: {(code ? ForCodeTranscript(r.ContentText) : r.ContentText)}"); break;
            }
        }
        return sb.ToString();
    }
}

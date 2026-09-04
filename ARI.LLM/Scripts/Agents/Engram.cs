using ARI.Common;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ARI.BrainVault;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Engram : MemoryAgent, IDisposable
{
    // ── Constants ────────────────────────────────────────────────────────────────
    private const int    EXTRACT_MAX_TOKENS   = 1500;
    private const double EXTRACT_TEMPERATURE = 0.3;
    private const int    SAVE_MAX_TOKENS      = 300;

    // Each Stage-3 call now places exactly one entity in its own short-lived thread, so there's no
    // multi-entity turn left to keep open — this and the ceiling below fall back to MemoryAgent's
    // defaults (single-commit-per-turn, 8-call breaker), which fit a one-note, one-tool call cleanly.
    // The old override existed because Engram used to recon a whole entity SET (find/search/read many
    // notes) before writing any of them inside one giant turn — that turn shape is gone.

    [JsonIgnore] internal Dialogue?    dialogue       { get; set; }
    [JsonIgnore] internal Context?     context        { get; set; }
    [JsonIgnore] internal string       PersistentDir  { get; set; } = string.Empty;

    // Completed exchanges an active thread accumulates before an interval sweep; 0 disables it.
    public int TurnsBeforeSweep { get; set; } = 5;

    private readonly Dictionary<string, DateTime>       lastRun          = new();
    private readonly Dictionary<string, int>            lastHistoryCount = new();
    private readonly ConcurrentDictionary<string, int>  exchangeCounts   = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim                      engramLock       = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> sweepingThreads  = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>                    pendingQueue     = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient                         httpClient       = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    // Per-thread count of exchanges, never reset by a sweep — "every N turns" means N wall-clock turns.
    internal bool ShouldIntervalSweep(string threadKey)
    {
        if (TurnsBeforeSweep <= 0) return false;
        int count = exchangeCounts.AddOrUpdate(threadKey, 1, (_, c) => c + 1);
        return count % TurnsBeforeSweep == 0;
    }

    private ConcurrentDictionary<string, Thread> threads = new();

    internal event Action<string>? SweepCompleted;

    internal bool IsSweeping(string threadKey) => sweepingThreads.ContainsKey(threadKey);

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

    internal void Init(Dialogue dialogue, Context? context, ConcurrentDictionary<string, Thread> threads)
    {
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
    internal async Task RunEngram(string threadKey, string trigger, bool force = false)
    {
        // Global dev kill switch — force-proof. In DevMode Engram never runs (not even on a manual
        // close, which passes force), so an autonomous run can never mutate the brain. The thread is
        // still marked processed so its deletion timer proceeds normally.
        if (Shared.DevMode)
        {
            if (threads.TryGetValue(threadKey, out Thread? devThread)) devThread.EngramProcessed = true;
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] skipped — DevMode is on.", threadKey);
            return;
        }

        // Guild threads never reach the brain. A Discord server is multi-party: anyone whitelisted can
        // speak into the transcript, and a sweep treats every line as material to store — so a guest's
        // provocation becomes a durable note about a third party, and guarded mode cannot help because it
        // filters recall, not writes. Owner-only filtering is not a substitute: the misfiled note that
        // prompted this gate was built from the OWNER's own messages, misread by pronoun resolution.
        // Force-proof like the DevMode gate above — a manual close must not be a way around it either.
        if (threadKey.StartsWith("guild:", StringComparison.Ordinal))
        {
            if (threads.TryGetValue(threadKey, out Thread? guildThread)) guildThread.EngramProcessed = true;
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] skipped — guild threads are never swept into the brain.", threadKey);
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
            List<ThreadItem> allItems = threads.TryGetValue(threadKey, out Thread? dialogueThread) ? dialogueThread.History : new List<ThreadItem>();
            List<ThreadItem> conversationItems = allItems.Where(i => i is LLM.Prompt or Response).ToList();

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
            transcriptSeen = BuildTranscript(recentItems);
            if (!await Classify(recentItems, trigger))
            {
                outcome   = "skipped — classified as task-only (or no new messages)";
                processed = true;   // nothing to save is still "processed" — the thread may be deleted
                return;
            }

            // recentItems is only what's new since lastHistoryCount — a 2nd+ sweep never re-reads the full transcript.
            string transcript = transcriptSeen.Length > 0 ? transcriptSeen : BuildTranscript(recentItems);
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
            List<ExtractedEntity> entities = await ExtractAsync(transcript, contextSummary);

            // The conversation's own date — not "today", since Engram can run well after the fact.
            // Stage 3 is told this explicitly so it never has to guess or invent one.
            DateTime logStart = recentItems.Count > 0 ? recentItems[0].Timestamp : DateTime.Now;
            DateTime logEnd   = recentItems.Count > 0 ? recentItems[^1].Timestamp : DateTime.Now;
            string   conversationDate = logStart.ToString("yyyy-MM-dd");

            // --- Stage 3: save everything from this conversation in one call. Each entity's note is
            //     still resolved by exact match beforehand and the write tool is still confined to that
            //     pre-resolved set of paths — one call now covers the whole sweep's entities instead of
            //     one call per entity, but no entity can still reach a note it wasn't resolved against. ---
            (int commits, int blocked) = await SaveAllAsync(threadKey, entities, speaker, conversationDate);

            // Deterministic, code-built log line — no extra LLM round trip just to summarise what the
            // extraction step already told us.
            if (entities.Count > 0)
            {
                string logLine = "Discussed: " + string.Join(", ", entities.Select(e => e.Entity).Distinct()) + ".";
                AppendToConversationLog(DateTime.Now.ToString("yyyy-MM-dd"), logLine, logStart, logEnd);
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
            engramLock.Release();
            SweepCompleted?.Invoke(threadKey);
        }
    }

    // Test-only: runs Classify + Extract like RunEngram, then stops — never touches Save or writes anything.
    internal async Task<List<(string Entity, bool IsNew, string Excerpt, bool Sensitive)>> DebugExtractOnly(string threadKey)
    {
        List<ThreadItem> allItems = threads.TryGetValue(threadKey, out Thread? dialogueThread) ? dialogueThread.History : new List<ThreadItem>();
        List<ThreadItem> conversationItems = allItems.Where(i => i is LLM.Prompt or Response).ToList();
        List<ThreadItem> recentItems = conversationItems;   // treat as a fresh, first-ever sweep

        if (!await Classify(recentItems, "debug-extract-only")) return new();

        string transcript = BuildTranscript(recentItems);
        string contextSummary = context?.GetContext(threadKey) ?? string.Empty;
        List<ExtractedEntity> entities = await ExtractAsync(transcript, contextSummary);
        return entities.Select(e => (e.Entity, e.IsNew, e.Excerpt, e.Sensitive)).ToList();
    }

    // ── Stage 2: extract ───────────────────────────────────────────────────────────────

    // Entity is always the plain subject name (a person or relationship), never an invented compound
    // title — that's what let two private notes about the same subject drift into two different titles
    // and never resolve to each other. Sensitive marks it as Private/ content; Save resolves the
    // deterministic Private/{Entity}.md path itself rather than the model inventing one.
    private readonly record struct ExtractedEntity(string Entity, bool IsNew, string Excerpt, bool Sensitive);

    /// <summary>One tool-free completion over the whole transcript: every entity worth remembering,
    /// regardless of whether it's already in the vault (Stage 3 checks that), each with the actual
    /// transcript lines it came from — not a paraphrase, so Stage 3 has real material to work from
    /// without needing to share a thread with this call.</summary>
    private async Task<List<ExtractedEntity>> ExtractAsync(string transcript, string contextSummary)
    {
        string context = string.IsNullOrWhiteSpace(contextSummary)
            ? ""
            : ResolveTemplate("ContextBlock", "", ("contextSummary", contextSummary));

        object requestBody = new
        {
            model    = "local",
            messages = new[]
            {
                new { role = "system", content = ResolveTemplate("ExtractSystem", "") + "\n<|think_off|>" },
                new { role = "user",   content = ResolveTemplate("ExtractTask", "", ("context", context), ("transcript", transcript)) }
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
    // first [...] span rather than requiring the whole completion to be pure JSON.
    private static List<ExtractedEntity> ParseExtracted(string content)
    {
        int start = content.IndexOf('[');
        int end   = content.LastIndexOf(']');
        if (start < 0 || end <= start) return new();

        try
        {
            using JsonDocument doc = JsonDocument.Parse(content[start..(end + 1)]);
            List<ExtractedEntity> results = new();
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                string entity    = el.TryGetProperty("entity",    out JsonElement e) ? e.GetString() ?? "" : "";
                string excerpt   = el.TryGetProperty("excerpt",   out JsonElement x) ? x.GetString() ?? "" : "";
                bool   isNew     = el.TryGetProperty("is_new",    out JsonElement n) && n.ValueKind == JsonValueKind.True;
                bool   sensitive = el.TryGetProperty("sensitive", out JsonElement s) && s.ValueKind == JsonValueKind.True;
                if (entity.Length > 0 && excerpt.Length > 0)
                    results.Add(new ExtractedEntity(entity, isNew, excerpt, sensitive));
            }
            return results;
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[Engram] Failed to parse extraction output ({Error}) — treating as nothing extracted.", ex.Message);
            return new();
        }
    }

    // ── Stage 3: save ──────────────────────────────────────────────────────────────────

    // One resolved entity ready for the Save call: its allowed write name(s)/prefix and the
    // existing-content block describing what (if anything) is already there.
    private readonly record struct ResolvedEntity(string Entity, string ExistingBlock, HashSet<string> AllowedNames, string AllowedPrefix);

    // Resolves one entity's note by exact match, never fuzzy search — a fuzzy match once let a write land on an unrelated note.
    private static ResolvedEntity ResolveEntity(ExtractedEntity entity)
    {
        Note? normalNote = Brain.GetNote(entity.Entity);
        HashSet<string> allowedNames = new(StringComparer.OrdinalIgnoreCase);

        if (entity.Sensitive)
        {
            // The " - Private" suffix avoids a title collision with the entity's own identity note (titles are unique vault-wide).
            string privateName = $"Private/{SanitizeNoteFileName(entity.Entity)} - Private";
            Note?  existing    = Brain.GetNote(privateName);
            allowedNames.Add(privateName);
            if (normalNote is not null) allowedNames.Add(normalNote.Name);   // rare migrate-out-of-normal-note case

            string block = existing is not null
                ? $"Already exists — call edit_memory(name: \"{privateName}\"):\n\n{existing.Content}\n\n" +
                  "Add this under the right topic subheading (or a new one if none fits) — don't create_memory a separate note for it."
                : $"Does not exist yet — call create_memory(name: \"{privateName}\") and link it out to [[Private]] (the hub).";
            // Kept to one short line — a longer explanation here measurably slowed Save down.
            if (normalNote is not null)
            {
                block += $" (Plain mentions of {entity.Entity} elsewhere link to \"{normalNote.Name}\", not here.)";
                // The private note needs an outward link from the profile, or it's unreachable from recall.
                bool alreadyLinked = normalNote.Content.Contains($"[[{privateName}", StringComparison.OrdinalIgnoreCase);
                if (!alreadyLinked)
                {
                    allowedNames.Add(normalNote.Name);
                    // old_string is computed here, not left to the model, so there's nothing for Save to mis-copy.
                    string trailingLine = normalNote.Content.TrimEnd().Split('\n').LastOrDefault() ?? "";
                    if (trailingLine.Length > 0)
                        block += $"\n\nMECHANICAL EDIT, no judgement needed: call edit_memory(name: \"{normalNote.Name}\", old_string: \"{EscapeForPrompt(trailingLine)}\", new_string: \"{EscapeForPrompt(trailingLine)}\\nSee [[{privateName}]].\") to add the link under the note's last heading. Do not touch anything else in the note.";
                }
            }
            return new ResolvedEntity(entity.Entity, block, allowedNames, privateName);
        }
        else
        {
            if (normalNote is not null) allowedNames.Add(normalNote.Name);
            string block = normalNote is not null
                ? $"Already exists — call edit_memory(name: \"{normalNote.Name}\"):\n\n{normalNote.Content}"
                : "Does not exist yet — call create_memory with a name/path chosen per the rulebook above (e.g. \"People/Name\").";
            // Lets Save split an oversized note into a sibling create_memory call in the same turn.
            return new ResolvedEntity(entity.Entity, block, allowedNames, normalNote?.Name ?? "");
        }
    }

    // Saves every entity in one call; edit_memory is confined to each entity's own pre-resolved name/prefix (see ResolveEntity).
    private async Task<(int Commits, int Blocked)> SaveAllAsync(string threadKey, List<ExtractedEntity> entities, string speaker, string conversationDate)
    {
        if (entities.Count == 0) return (0, 0);

        Brain.Index();   // pick up any commits made just before this sweep started
        List<ResolvedEntity> resolved = entities.Select(ResolveEntity).ToList();

        HashSet<string> allowedNames    = new(StringComparer.OrdinalIgnoreCase);
        List<string>    allowedPrefixes = new();
        StringBuilder   entityBlocks    = new();
        for (int i = 0; i < resolved.Count; i++)
        {
            ResolvedEntity r = resolved[i];
            foreach (string n in r.AllowedNames) allowedNames.Add(n);
            if (r.AllowedPrefix.Length > 0) allowedPrefixes.Add(r.AllowedPrefix);
            entityBlocks.Append($"### {r.Entity}\n{r.ExistingBlock}\n\nWhat was said (speaker: {speaker}):\n{entities[i].Excerpt}\n\n");
        }

        Thread mini = new(ThreadPipeline.Dialogue, $"engram:{threadKey}:{Guid.NewGuid():N}") { Internal = true };
        mini.FilesystemRoot = Brain.VaultRoot;
        mini.IsBrainVault   = true;
        mini.Ct             = CancellationToken.None;
        // create_memory/edit_memory, never the generic filesystem tools — each commits itself, one note at a time.
        new CreateMemory().Register(mini);
        new EditMemory(allowedNames: allowedNames, allowedPrefixes: allowedPrefixes).Register(mini);
        PublishForInspection(mini);

        string task = ResolveTemplate("SaveTask", "",
            ("entities", entityBlocks.ToString().TrimEnd()),
            ("date",     conversationDate));

        await Prompt(mini, task, new PromptOptions
        {
            Username = "system",
            OnDelta  = async _ => { Notify?.Invoke(mini.Key); await Task.CompletedTask; },
        });

        // Pairs each tool_call to its next same-name tool_result so one turn covering several entities still reports per-note outcomes.
        List<TraceStep> trace = mini.History.OfType<Response>().SelectMany(r => r.Trace ?? Enumerable.Empty<TraceStep>()).ToList();
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

    // Filenames can't carry the taxonomy's " — " em-dash convention verbatim if the source has path-
    // hostile characters (rare, but an entity name is model output) — strip anything a filesystem would
    // reject rather than let a bad character break the write.
    private static string SanitizeNoteFileName(string entity) =>
        string.Concat(entity.Split(Path.GetInvalidFileNameChars())).Trim();

    // ── Lightweight code-thread summary ────────────────────────────────────────────────

    /// <summary>Summarises a coding thread into 1-2 paragraphs and appends it to today's conversation log.
    /// No memory extraction, no graph walk — just a record of what was worked on.</summary>
    internal async Task RunCodeSummary(string threadKey, string trigger)
    {
        if (Shared.DevMode)
        {
            if (threads.TryGetValue(threadKey, out Thread? devThread)) devThread.EngramProcessed = true;
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] code summary skipped — DevMode is on.", threadKey);
            return;
        }

        if (!IsEnabled)
        {
            lock (pendingQueue) pendingQueue.Add(threadKey);
            return;
        }
        if (!await engramLock.WaitAsync(0)) return;
        sweepingThreads[threadKey] = 0;

        string outcome = "incomplete (unexpected exit)";
        bool processed = false;

        try
        {
            if (!threads.TryGetValue(threadKey, out Thread? codeThread)) { processed = true; return; }
            List<ThreadItem> items = codeThread.History.Where(i => i is LLM.Prompt or Response).ToList();
            if (items.Count == 0) { outcome = "skipped — no messages"; processed = true; return; }

            string transcript = BuildTranscript(items);
            string speaker = items.OfType<Prompt>().Select(p => p.AuthorName)
                .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
                ?? ReadStoredUserName() ?? "the user";

            string summary = await GenerateCodeSummary(transcript, speaker);
            if (string.IsNullOrWhiteSpace(summary)) { outcome = "skipped — empty summary"; processed = true; return; }

            string date = DateTime.Now.ToString("yyyy-MM-dd");
            AppendToConversationLog(date, summary, items[0].Timestamp, items[^1].Timestamp, "Coding session");

            outcome   = "code summary saved";
            processed = true;
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] code summary saved to Conversations/{Date}.", threadKey, date);
        }
        finally
        {
            SessionRecorder.StandaloneNote("Engram", $"engram-code:{threadKey}", "code-summary", new Dictionary<string, object?>
            {
                ["trigger"]   = trigger,
                ["thread"]    = threadKey,
                ["outcome"]   = outcome,
                ["processed"] = processed,
            });
            if (processed && threads.TryGetValue(threadKey, out Thread? pt)) pt.EngramProcessed = true;
            sweepingThreads.TryRemove(threadKey, out _);
            engramLock.Release();
            SweepCompleted?.Invoke(threadKey);
        }
    }

    private async Task<string> GenerateCodeSummary(string transcript, string speaker)
    {
        object requestBody = new
        {
            model    = "local",
            messages = new[]
            {
                new { role = "system", content = "You summarise coding sessions. Write 1-2 short paragraphs describing what was worked on: the project, features implemented, bugs fixed, and any notable decisions. Write in past tense, third person (refer to the user by name). No bullet points, no code snippets. Be concise.\n\nWrap every named entity (person, project, tool, service, library, feature) in [[wikilinks]] so the memory graph can link them. Examples: [[Xywren]], [[ARI.UI]], [[PureBill]], [[Engram]], [[pipeline selector]]. If unsure whether something deserves a link, link it.\n<|think_off|>" },
                new { role = "user",   content = $"Summarise this coding session by {speaker}:\n\n{transcript}" }
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
            Shared.Logger.LogWarning("[Engram] Code summary generation failed: {Err}", ex.Message);
            return "";
        }
    }

    // Each entry carries the actual time range of that conversation (first message → last message),
    // rather than one undifferentiated blob per day — several separate conversations on the same day
    // stay distinguishable.
    private static void AppendToConversationLog(string date, string summary, DateTime start, DateTime end, string label = "Conversation")
    {
        string path = Path.Combine(Brain.VaultRoot, "Conversations", $"{date}.md");
        string existing = File.Exists(path) ? File.ReadAllText(path) : "";

        string timeRange = start.ToString("HH:mm") == end.ToString("HH:mm")
            ? start.ToString("HH:mm")
            : $"{start:HH:mm}–{end:HH:mm}";
        string entry = $"\n\n---\n### {timeRange} — {label}\n{summary}";
        if (existing.Length > 0)
        {
            File.AppendAllText(path, entry);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"# {date}{entry}");
        }
        Brain.Index();
        GitCommitBrain($"Conversation log: {label.ToLowerInvariant()} — {date}", Path.Combine("Conversations", $"{date}.md"));
    }

    // relativePath scopes the commit to exactly the touched file — `git add -A` would sweep in unrelated dirty state.
    private static void GitCommitBrain(string message, string relativePath)
    {
        try
        {
            string vault = Brain.VaultRoot;
            RunGit(vault, "add", "--", relativePath);
            RunGit(vault, "commit", "-m", message);
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[Engram] Git commit for code summary failed: {Err}", ex.Message);
        }
    }

    private static void RunGit(string workDir, params string[] args)
    {
        System.Diagnostics.ProcessStartInfo psi = new()
        {
            FileName               = "git",
            WorkingDirectory       = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
    }

    // ── Classify (unchanged) ─────────────────────────────────────────────────────────

    private async Task<bool> Classify(IReadOnlyList<ThreadItem> recentItems, string trigger)
    {
        string transcript = BuildTranscript(recentItems);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            Shared.Logger.LogInformation("[Engram] [{Trigger}] no new messages to classify, skipping.", trigger);
            return false;
        }

        object requestBody = new
        {
            model    = "local",
            messages = new[]
            {
                // <|think_off|> is appended in code, not stored with the prompt: it is a model control
                // token, not prose, and editing it would silently stop think-off rather than reword anything.
                new { role = "system", content = ResolveTemplate("ClassifierSystem", "") + "\n<|think_off|>" },
                new { role = "user",   content = ResolveTemplate("ClassifierTask", "", ("transcript", transcript)) }
            },
            stream      = false,
            max_tokens  = 5,
            temperature = 0.0,
            // Without these the template force-opens a <think> block and the model burns all 5
            // tokens inside it — content never contains "yes", so every sweep classified as task-only.
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

            string responseJson = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(responseJson);
            string answer = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            bool worthStoring = answer.Trim().StartsWith("yes", StringComparison.OrdinalIgnoreCase);
            if (!worthStoring)
                Shared.Logger.LogInformation("[Engram] [{Trigger}] classified as task-only, skipping extraction.", trigger);
            return worthStoring;
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[Engram] Classification failed ({Error}), proceeding with extraction.", ex.Message);
            return true;
        }
    }

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

    // For inline display in a prompt instruction, not JSON serialisation — just needs to read unambiguously.
    private static string EscapeForPrompt(string text) => text.Replace("\"", "\\\"");

    private static string BuildTranscript(IEnumerable<ThreadItem> items)
    {
        StringBuilder sb = new();
        foreach (ThreadItem item in items)
        {
            switch (item)
            {
                case Prompt u: sb.AppendLine($"{u.AuthorName}: {u.Text}"); break;
                case Response r: sb.AppendLine($"ARI: {r.ContentText}");      break;
            }
        }
        return sb.ToString();
    }
}

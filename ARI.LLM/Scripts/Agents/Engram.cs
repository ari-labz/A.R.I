using ARI.Common;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Engram : MemoryAgent, IDisposable
{
    // Each Stage-3 call now places exactly one entity in its own short-lived thread, so there's no
    // multi-entity turn left to keep open — this and the ceiling below fall back to MemoryAgent's
    // defaults (single-commit-per-turn, 8-call breaker), which fit a one-note, one-tool call cleanly.
    // The old override existed because Engram used to recon a whole entity SET (find/search/read many
    // notes) before writing any of them inside one giant turn — that turn shape is gone.

    [JsonIgnore] internal Dialogue?    dialogue       { get; set; }
    [JsonIgnore] internal Context?     context        { get; set; }
    [JsonIgnore] internal string       PersistentDir  { get; set; } = string.Empty;

    private readonly Dictionary<string, DateTime>       lastRun          = new();
    private readonly Dictionary<string, int>            lastHistoryCount = new();
    private readonly SemaphoreSlim                      engramLock       = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> sweepingThreads  = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>                    pendingQueue     = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient                         httpClient       = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

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

    internal int PurgeNotes() => BrainModule.PurgeAllNotes();

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

            string transcript = BuildTranscript(conversationItems);
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

            // --- Stage 3: place each entity independently — a short, scoped call per entity instead of
            //     one long wandering thread, so context stays flat (no compounding prefill) and a write
            //     structurally cannot reach a note other than the one it's about. ---
            int commits = 0, blocked = 0;
            foreach (ExtractedEntity entity in entities)
            {
                switch (await PlaceEntityAsync(threadKey, entity, speaker, conversationDate))
                {
                    case PlacementOutcome.Committed: commits++; break;
                    case PlacementOutcome.Blocked:   blocked++; break;
                }
            }

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

    // ── Stage 2: extract ───────────────────────────────────────────────────────────────

    // Subject is set only when Entity is a split-off from a normal entity (e.g. Entity "Alex —
    // Private Topic", Subject "Alex") — it tells Stage 3 which second note it may also touch,
    // resolved by the same exact match as Entity itself, so a genuine move-a-section-out edit can
    // update both notes without ever reaching a third, unresolved one.
    private readonly record struct ExtractedEntity(string Entity, bool IsNew, string Excerpt, string? Subject);

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
            max_tokens  = 1500,
            temperature = 0.3,
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
                string  entity  = el.TryGetProperty("entity",  out JsonElement e) ? e.GetString() ?? "" : "";
                string  excerpt = el.TryGetProperty("excerpt", out JsonElement x) ? x.GetString() ?? "" : "";
                bool    isNew   = el.TryGetProperty("is_new",  out JsonElement n) && n.ValueKind == JsonValueKind.True;
                string? subject = el.TryGetProperty("subject", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                if (entity.Length > 0 && excerpt.Length > 0)
                    results.Add(new ExtractedEntity(entity, isNew, excerpt, string.IsNullOrWhiteSpace(subject) ? null : subject));
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

    private enum PlacementOutcome { Committed, NoChange, Blocked }

    /// <summary>Places one entity: a short, fresh call, tool-scoped to only the note(s) already resolved
    /// by exact match for this call. Looks the note(s) up in code (no search_brain round trip) and hands
    /// their current content straight to the model rather than making it re-discover them through tools.</summary>
    private async Task<PlacementOutcome> PlaceEntityAsync(string threadKey, ExtractedEntity entity, string speaker, string conversationDate)
    {
        BrainModule.Index();   // pick up commits from entities already placed earlier this same sweep
        // Exact lookup by title/alias/path (BrainModule.GetNote -> Database.FindNote), not the fuzzy
        // ranked content search (BrainModule.Search) search_brain uses for exploratory discovery. The
        // fuzzy search was the actual cause of writes landing on the wrong note when an entity name had
        // any incidental word overlap with something unrelated — the write-scope guard then faithfully
        // protected that wrong note instead of the right one. An exact match is the only safe basis for
        // treating a note as "this entity already exists"; anything less confident should read as new.
        Note? existing = BrainModule.GetNote(entity.Entity);

        HashSet<string> allowedPaths = new(StringComparer.OrdinalIgnoreCase);
        if (existing is not null) allowedPaths.Add(existing.Path);

        string existingBlock = existing is not null
            ? $"The note already exists at '{existing.Path}':\n\n{existing.Content}"
            : "No note exists yet for this entity — create one at an appropriate path per the rulebook above.";

        // A subject means this is a private split-off (per the rulebook, subject is only ever set for
        // Private/ content) — so unlike a normal new entity, its path isn't left for the model to pick:
        // it's deterministic, same as any other exact-match resolution, which lets it join the allowed
        // set even when the note doesn't exist yet. The subject's own note, when it resolves, is added
        // too — the model may genuinely need to trim a moved section out of it, not just link to it.
        string subjectBlock = "";
        if (entity.Subject is { } subjectName)
        {
            string newPrivatePath = existing?.Path ?? $"Private/{SanitizeNoteFileName(entity.Entity)}.md";
            allowedPaths.Add(newPrivatePath);

            Note? subjectNote = BrainModule.GetNote(subjectName);
            if (subjectNote is not null)
            {
                allowedPaths.Add(subjectNote.Path);
                subjectBlock = $"This is a split-off from '{subjectName}', at '{subjectNote.Path}':\n\n{subjectNote.Content}\n\n" +
                    $"You may write BOTH '{newPrivatePath}' (this entity) and '{subjectNote.Path}' (the subject) if content needs to move out " +
                    $"of the subject note into this one — link the subject note outward to this one, and if you remove content from it, reword " +
                    $"the surrounding prose so nothing dangles. If the subject note doesn't need to change, only write this entity's note.\n";
            }
        }

        Thread mini = new(ThreadPipeline.Dialogue, $"engram:{threadKey}:{Guid.NewGuid():N}") { Internal = true };
        mini.FilesystemRoot = BrainModule.VaultRoot;
        mini.IsBrainVault   = true;
        mini.Ct             = CancellationToken.None;
        ServerFileSystem fs = new(BrainModule.VaultRoot, CancellationToken.None, brainVault: true);
        // A brand-new, non-private entity has no resolved path and nothing existing to protect, so it's
        // left unscoped (the model states its own path, per the taxonomy rules already in its persistent
        // context) — at worst a wrongly-placed new note, a normal tidy-up, not a loss. Every other case
        // (existing note, or any private split-off — new or not) is restricted to the paths resolved above.
        IReadOnlyCollection<string>? scope = (existing is null && entity.Subject is null) ? null : allowedPaths;
        new WriteFile(fs, allowedPaths: scope).Register(mini);
        PublishForInspection(mini);

        string task = ResolveTemplate("SaveTask", "",
            ("entity",        entity.Entity),
            ("existing",      existingBlock),
            ("subject_block", subjectBlock),
            ("excerpt",       entity.Excerpt),
            ("speaker",       speaker),
            ("date",          conversationDate));

        await Prompt(mini, task, new PromptOptions
        {
            Username = "system",
            OnDelta  = async _ => { Notify?.Invoke(mini.Key); await Task.CompletedTask; },
        });

        List<TraceStep> writeResults = mini.History.OfType<Response>()
            .SelectMany(r => r.Trace ?? Enumerable.Empty<TraceStep>())
            .Where(s => s.Kind == "tool_result" && s.Name == "write_file")
            .ToList();

        bool wrote   = writeResults.Any(s => !(s.Text?.StartsWith("[Blocked]", StringComparison.Ordinal) ?? true));
        bool blocked = !wrote && writeResults.Any(s => s.Text?.StartsWith("[Blocked]", StringComparison.Ordinal) ?? false);

        if (blocked)
        {
            // The scope guard fired against a real attempt — distinct from the model simply deciding
            // nothing needed to change, and worth knowing about even though nothing was lost (the write
            // just didn't land). Silent here is how the same failure mode would go unnoticed again.
            Shared.Logger.LogWarning("[Engram] [{ThreadKey}] write blocked for entity '{Entity}' — scope guard rejected the path.", threadKey, entity.Entity);
            return PlacementOutcome.Blocked;
        }
        if (!wrote) return PlacementOutcome.NoChange;

        string message = entity.Subject is { } subj
            ? $"Engram: update {entity.Entity} (split from {subj}) — {threadKey}"
            : $"Engram: update {entity.Entity} — {threadKey}";
        GitCommitBrain(message);
        return PlacementOutcome.Committed;
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
            max_tokens  = 300,
            temperature = 0.3,
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
        string path = Path.Combine(BrainModule.VaultRoot, "Conversations", $"{date}.md");
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
        BrainModule.Index();
        GitCommitBrain($"Conversation log: coding session {date}");
    }

    private static void GitCommitBrain(string message)
    {
        try
        {
            string vault = BrainModule.VaultRoot;
            RunGit(vault, "add", "-A");
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

using ARI.Common;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Engram : BrainAgent, IDisposable
{
    private const int ENGRAM_TRIGGER_DELAY = 5;

    [JsonIgnore] internal Dialogue?    dialogue       { get; set; }
    [JsonIgnore] internal Context?     context        { get; set; }

    private EngramBuffer? buffer;

    [JsonPropertyName("sweepIntervalMinutes")] public int SweepIntervalMinutes { get; init; }

    private readonly Dictionary<string, DateTime>       lastRun          = new();
    private readonly Dictionary<string, int>            lastHistoryCount = new();
    private readonly SemaphoreSlim                      engramLock       = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> sweepingThreads  = new(StringComparer.OrdinalIgnoreCase);
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

        buffer = new EngramBuffer(dialogue, this, threads);

        dialogue.ThreadBufferFull += threadKey =>
        {
            buffer.Remove(threadKey);
            _ = Task.Delay(TimeSpan.FromSeconds(ENGRAM_TRIGGER_DELAY)).ContinueWith(_ => RunEngram(threadKey, "chat buffer"));
        };

        dialogue.ThreadDeleted += threadKey =>
        {
            lastRun.Remove(threadKey);
            lastHistoryCount.Remove(threadKey);
            buffer.Remove(threadKey);
        };
    }

    internal bool IsEnabled { get; private set; } = true;

    internal void Enable()
    {
        IsEnabled = true;
        Shared.Logger.LogInformation("[Engram] Enabled.");
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

    internal async Task RunEngram(string threadKey, string trigger)
    {
        if (!IsEnabled) return;
        if (!await engramLock.WaitAsync(0)) return;
        sweepingThreads[threadKey] = 0;

        // --- Run-log capture (Logs): every sweep records its full thought process for offline analysis. ---
        List<(string Title, Thread Thread)> writeThreads = new();
        List<string>                       runMeta      = new() { $"Trigger: {trigger}", $"Thread: {threadKey}" };
        string                             outcome      = "incomplete (unexpected exit)";
        List<NoteChange>                   queueChanges = new();

        try
        {
            List<ThreadItem> allItems = threads.TryGetValue(threadKey, out Thread? dialogueThread) ? dialogueThread.History : new List<ThreadItem>();
            List<ThreadItem> conversationItems = allItems.Where(i => i is Prompt or Response).ToList();

            int lastCount = lastHistoryCount.TryGetValue(threadKey, out int c) ? c : 0;
            List<ThreadItem> recentItems = conversationItems.Skip(lastCount).ToList();

            lastRun[threadKey]          = DateTime.UtcNow;
            lastHistoryCount[threadKey] = conversationItems.Count;

            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] sweep triggered (trigger: {Trigger})", threadKey, trigger);

            // --- Phase 1: Classify ---
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] phase 1 — classifying conversation...", threadKey);
            runMeta.Add($"Classified transcript: {RunLogger.Trunc(BuildTranscript(recentItems), 600)}");
            if (!await Classify(recentItems, trigger))
            {
                outcome = "skipped — classified as task-only (or no new messages)";
                return;
            }

            string transcript = BuildTranscript(conversationItems);
            if (context is not null) await context.RebuildFromTranscript(threadKey, transcript);
            string contextSummary = context?.GetContext(threadKey) ?? string.Empty;

            // --- Phase 2: Gather mentions (no note-path dump — Search handles dedup later) ---
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] phase 2 — gathering entity mentions...", threadKey);
            Thread gatherThread = NewPhaseThread($"engram-gather-{threadKey}");
            string gatherRaw = await SendPrompt(gatherThread, GatherPrompt(transcript, contextSummary), thinkingBudgetOverride: THINKING_BUDGET);
            List<EntityMention> mentions = ParseMentions(gatherRaw);
            writeThreads.Add(("Gather mentions", gatherThread));

            // Always resolve the SPEAKER as an entity — gather often omits the user themselves, which is how
            // a self-duplicate gets created (their note exists under one name, the plan invents another from
            // the username). Injecting them forces search-then-judge to surface their real person note.
            string? speaker = conversationItems.OfType<Prompt>().Select(p => p.AuthorName).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
            if (speaker is not null)
            {
                mentions = mentions.Where(m => !m.Name.Equals(speaker, StringComparison.OrdinalIgnoreCase)).ToList();
                mentions.Insert(0, new EntityMention(speaker, new[] { speaker }, "the user/speaker in this conversation — resolve to their existing person note", IsSpeaker: true));
            }

            // Always include today's conversation log as a mention so it gets created/edited every sweep.
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            mentions = mentions.Append(new EntityMention(today, new[] { today }, "today's conversation log")).ToList();

            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] {Count} mention(s): [{Names}]",
                threadKey, mentions.Count, string.Join(", ", mentions.Select(m => m.Name)));

            if (mentions.Count == 0)
            {
                outcome = "no changes — no mentions gathered";
                return;
            }

            // --- Phase 3: Resolve candidates (search-then-judge, bounded regardless of vault size) ---
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] phase 3 — resolving candidates...", threadKey);
            List<CandidatePlan> resolved = await ResolveCandidates(mentions);

            // --- Phase 4: Plan ---
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] phase 4 — planning...", threadKey);
            Thread planThread = NewPhaseThread($"engram-plan-{threadKey}");
            string planRaw = await SendPrompt(planThread, PlanPrompt(transcript, contextSummary, resolved), thinkingBudgetOverride: THINKING_BUDGET);
            List<BrainPlanItem> plan = ParsePlan(planRaw);
            writeThreads.Add(("Plan", planThread));

            if (plan.Count == 0)
            {
                outcome = "no changes — plan was empty";
                return;
            }

            string planSummary = string.Join(", ", plan.Select(p => string.IsNullOrWhiteSpace(p.NewName) ? $"{p.Name} ({p.Op})" : $"{p.Name} -> {p.NewName} ({p.Op})"));
            runMeta.Add($"Plan ({plan.Count}): {RunLogger.Trunc(planSummary, 600)}");
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] plan: {Count} change(s) — [{Notes}]", threadKey, plan.Count, planSummary);

            // --- Phase 5: Batched write + apply (shared with Refactor) ---
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] phase 5 — writing {Count} note(s)...", threadKey, plan.Count);
            BrainWriter.ApplyResult result = await WritePlan(plan, EngramRulesPreamble(transcript, contextSummary), CancellationToken.None);
            queueChanges = result.Changes;

            // Deterministic structural repairs: fold any drifted title variant back into its base
            // ("Xywren — User" → "Xywren"), create hub notes for populated folders, link every hub down
            // to its members, then de-link anything left unresolved.
            BrainModule.MergeTitleVariants();
            BrainModule.EnsureHubNotes();
            BrainModule.EnsureHubChildLinks();
            int delinked = BrainModule.StripUnresolvedLinks();
            if (delinked > 0) Shared.Logger.LogInformation("[Engram] [{ThreadKey}] de-linked unresolved references in {Count} note(s).", threadKey, delinked);

            outcome = $"{result.Succeeded} saved, {result.Failed} failed";
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] phase 5 complete: {Success} saved, {Fail} failed.", threadKey, result.Succeeded, result.Failed);

            if (queueChanges.Count > 0)
            {
                Shared.Logger.LogInformation("[Engram] [{ThreadKey}] {Count} note change(s): {Changes}",
                    threadKey, queueChanges.Count, string.Join(", ", queueChanges.Select(ch => $"{ch.Op}:{ch.Title}")));

                // Rendered client-side as the "A·R·I will remember this" collapsible block.
                if (threads.TryGetValue(threadKey, out Thread? liveThread))
                    liveThread.AddItem(new EngramEvent { Changes = queueChanges });
            }

            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] sweep complete.", threadKey);
        }
        finally
        {
            runMeta.Add($"Outcome: {outcome}");
            RunLogger.Write("Engram", threadKey, writeThreads, runMeta);

            sweepingThreads.TryRemove(threadKey, out _);
            engramLock.Release();
            SweepCompleted?.Invoke(threadKey);
        }
    }

    // ── Prompts ──────────────────────────────────────────────────────────────────────

    private static string GatherPrompt(string transcript, string contextSummary)
    {
        StringBuilder sb = new();
        sb.AppendLine("Read this conversation and list every entity (person, place, event, project, thing) worth remembering.");
        sb.AppendLine("You do not check whether it already has a note — that happens later.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(contextSummary))
            sb.AppendLine($"CONTEXT SUMMARY (resolve all pronouns with this):\n{contextSummary}\n");
        sb.AppendLine($"CONVERSATION:\n{transcript}");
        sb.AppendLine();
        sb.AppendLine("List EVERY real person, place, event, project, pet, or thing NAMED IN THE CONVERSATION ABOVE " +
                       "— do not skip any, even when many are named. Do NOT include yourself (Ari/ARI, the assistant), " +
                       "and do NOT invent entities for abstract topics (\"big news\", \"an update\") — only concrete, named things.");
        sb.AppendLine();
        sb.AppendLine("Each entry: name = the entity's actual name from the conversation; terms = 2-4 words to find it " +
                       "later (nicknames, roles, related names); context = why it matters.");
        sb.AppendLine("Output ONLY a JSON object: a key \"mentions\" whose value is an array of such entries. Use the " +
                       "REAL names from the conversation — never placeholder text, brackets, or example words.");
        sb.AppendLine("If the conversation names nothing worth remembering: {\"mentions\": []}");
        return sb.ToString();
    }

    private static string PlanPrompt(string transcript, string contextSummary, List<CandidatePlan> resolved)
    {
        StringBuilder sb = new();
        sb.AppendLine(BrainRulebook.RULES);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(contextSummary))
            sb.AppendLine($"Use the context summary to resolve all pronouns:\n{contextSummary}\n");
        sb.AppendLine("Based on the conversation and these resolved entities, list every note to create or edit.");
        sb.AppendLine();
        sb.AppendLine($"CONVERSATION:\n{transcript}");
        sb.AppendLine();
        sb.AppendLine("RESOLVED ENTITIES:");
        foreach (CandidatePlan candidate in resolved)
        {
            if (candidate.ExistingNote is null)
                sb.AppendLine($"- NEW entity \"{candidate.Mention.Name}\" (no existing note): {candidate.Mention.Context}");
            else
                // Lead with the EXISTING note's identity, not the spoken name, so the plan targets the
                // real note. The spoken name is demoted to a parenthetical the model must not turn into a path.
                sb.AppendLine($"- EDIT existing note titled \"{candidate.ExistingNote.Title}\" at path \"{candidate.ExistingNote.Name}\" " +
                               $"(the user said \"{candidate.Mention.Name}\", which is the SAME note — do NOT create a \"{candidate.Mention.Name}\" note): {candidate.Mention.Context}");
        }
        sb.AppendLine();
        sb.AppendLine("For any EDIT entity above, `name` MUST be its stated existing path exactly. Never invent a new path from the spoken name.");
        sb.AppendLine();
        sb.AppendLine("For each provide: op (\"add\"/\"edit\"), name (desired full path — CURRENT path if editing), " +
                       "summary (1-2 sentences — key facts, pronouns, main links), newName (only if an existing note is moving).");
        sb.AppendLine();
        sb.AppendLine("Output ONLY:");
        sb.AppendLine("{\"plan\": [{\"op\": \"add\", \"name\": \"People/Fenn\", \"summary\": \"...\"}]}");
        sb.AppendLine("If nothing needs to be stored: {\"plan\": []}");
        return sb.ToString();
    }

    private static string EngramRulesPreamble(string transcript, string contextSummary)
    {
        StringBuilder sb = new();
        sb.AppendLine(BrainRulebook.RULES);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(contextSummary))
            sb.AppendLine($"CONTEXT SUMMARY:\n{contextSummary}\n");
        sb.AppendLine($"CONVERSATION (for reference while writing):\n{transcript}");
        return sb.ToString();
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
                new { role = "system", content = "You classify whether a conversation contains information worth storing as a long-term memory.\n<|think_off|>" },
                new { role = "user",   content =
                    "Does the following conversation contain anything worth remembering long-term — a personal " +
                    "fact, relationship, event, or world knowledge about the user; OR something revealing about " +
                    "them worth noting (how they felt or reacted, a behavioural pattern, or something to follow " +
                    "up on later)?\n\n" +
                    "Only a purely task-focused exchange (coding, debugging, technical problem-solving, or general " +
                    "Q&A with no personal content) does NOT qualify.\n\n" +
                    $"CONVERSATION:\n{transcript}\n\n" +
                    "Respond with only 'yes' or 'no'." }
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

    // ── Parsing ──────────────────────────────────────────────────────────────────────

    private static List<EntityMention> ParseMentions(string raw)
    {
        try
        {
            raw = raw.Trim();
            int start = raw.IndexOf('{');
            if (start >= 0) raw = raw[start..];
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("mentions", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
                return new();

            List<EntityMention> mentions = new();
            foreach (JsonElement el in arr.EnumerateArray())
            {
                string? name = el.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                // Safety net: drop echoed placeholder text ("<entity name>") and the assistant itself.
                if (name.Contains('<') || name.Contains('>')) continue;
                if (name.Equals("Ari", StringComparison.OrdinalIgnoreCase) || name.Equals("ARI", StringComparison.OrdinalIgnoreCase)) continue;
                List<string> terms = el.TryGetProperty("terms", out JsonElement t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                    : new List<string> { name };
                if (terms.Count == 0) terms = new List<string> { name };
                string context = el.TryGetProperty("context", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : string.Empty;
                mentions.Add(new EntityMention(name, terms, context));
            }
            return mentions;
        }
        catch (Exception ex)
        {
            Shared.Logger.LogError("[Engram] Failed to parse mentions: {Error}. Raw: {Raw}", ex.Message, raw);
            return new();
        }
    }

    private static List<BrainPlanItem> ParsePlan(string raw)
    {
        try
        {
            raw = raw.Trim();
            int start = raw.IndexOf('{');
            if (start >= 0) raw = raw[start..];
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("plan", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
                return new();

            List<BrainPlanItem> items = new();
            foreach (JsonElement el in arr.EnumerateArray())
            {
                string? op      = el.GetString("op");
                string? name    = el.GetString("name");
                string? summary = el.GetString("summary");
                string? newName = el.GetString("newName");
                if (!string.IsNullOrWhiteSpace(op) && !string.IsNullOrWhiteSpace(name))
                    items.Add(new BrainPlanItem(op, name, summary ?? string.Empty, newName));
            }
            return items;
        }
        catch (Exception ex)
        {
            Shared.Logger.LogError("[Engram] Failed to parse plan: {Error}. Raw: {Raw}", ex.Message, raw);
            return new();
        }
    }
}

internal class EngramBuffer
{
    private const int DRAIN_QUEUE_LIMIT = 10;

    private readonly Dialogue      dialogue;
    private readonly Engram        engram;
    private readonly ConcurrentDictionary<string, Thread> threads;
    private readonly Queue<string> queue            = new();
    private readonly HashSet<string> queuedKeys     = new();

    internal EngramBuffer(Dialogue dialogue, Engram engram, ConcurrentDictionary<string, Thread> threads)
    {
        this.dialogue = dialogue;
        this.engram   = engram;
        this.threads  = threads;

        dialogue.ThreadBecameInactive += threadKey =>
        {
            if (queuedKeys.Contains(threadKey)) return;
            queue.Enqueue(threadKey);
            queuedKeys.Add(threadKey);

            if (queue.Count >= DRAIN_QUEUE_LIMIT)
                _ = Task.Run(Drain);
            else if (!threads.Values.Any(t => t.Pipeline == ThreadPipeline.Dialogue && t.State is ThreadState.Idle or ThreadState.Streaming))
                _ = Task.Run(Drain);
        };
    }

    internal void Remove(string threadKey) => queuedKeys.Remove(threadKey);

    internal async Task Drain()
    {
        while (true)
        {
            if (queue.Count == 0) return;
            string threadKey = queue.Dequeue();
            queuedKeys.Remove(threadKey);

            threads.TryGetValue(threadKey, out Thread? thread);
            if (thread is null || thread.State is ThreadState.Idle or ThreadState.Streaming or ThreadState.Deleted)
                continue;

            await engram.RunEngram(threadKey, "inactivity");
            if (threads.TryGetValue(threadKey, out Thread? et)) et.MarkEngramProcessed();

            if (threads.Values.Any(t => t.Pipeline == ThreadPipeline.Dialogue && t.State is ThreadState.Idle or ThreadState.Streaming))
                return;
        }
    }
}

file static class JsonElementExtensions
{
    internal static string? GetString(this JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

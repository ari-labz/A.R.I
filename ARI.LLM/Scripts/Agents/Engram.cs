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
    // Engram places several memories from one conversation in a single turn, so it does NOT end after
    // the first commit (that's the Refactor walk's behaviour).
    internal override bool StopAfterCommit => false;

    // No work-call ceiling: the circuit breaker exists for the single-change Refactor epoch. Engram must
    // recon several existing entities (find/search/read) before it can place memories, so an 8-call cap
    // guillotines the sweep during exploration — it never reaches write_file/git_commit. Disable it here.
    internal override int? EpochToolCeiling => null;

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

            // --- Tool-driven placement: the agent walks the graph and stores the memories itself. ---
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] placing memories via graph walk...", threadKey);
            Thread parent = new(ThreadPipeline.Dialogue, $"engram:{threadKey}:{Guid.NewGuid():N}") { Internal = true };
            RegisterTools(parent, PersistentDir, CancellationToken.None);
            PublishForInspection(parent);   // surface the sweep in the DTI
            placementKey = parent.Key;

            await Prompt(parent, EngramTask(transcript, contextSummary, speaker), new PromptOptions
            {
                Username = "system",
                OnDelta  = async _ => { Notify?.Invoke(parent.Key); await Task.CompletedTask; },
            });

            int commits = parent.History.OfType<Response>()
                .SelectMany(r => r.Trace ?? Enumerable.Empty<TraceStep>())
                .Count(s => s.Kind == "tool_result" && s.Name == "git_commit"
                            && (s.Text?.StartsWith("Committed", StringComparison.Ordinal) ?? false));
            committed = commits;
            outcome   = $"{commits} memory change(s) committed";
            processed = true;
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] sweep complete — {Commits} change(s).", threadKey, commits);
        }
        finally
        {
            SessionRecorder.StandaloneNote("Engram", placementKey.Length > 0 ? placementKey : $"engram:{threadKey}", "sweep", new Dictionary<string, object?>
            {
                ["trigger"]             = trigger,
                ["swept_thread"]        = threadKey,
                ["classified_transcript"] = transcriptSeen,
                ["commits"]             = committed,
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

    // ── Placement task ─────────────────────────────────────────────────────────────────

    // The task turn, templated from Agents.json. The context block is its own entry so that an empty
    // context emits nothing at all rather than a bare "CONTEXT:" header.
    private string EngramTask(string transcript, string contextSummary, string speaker)
    {
        string context = string.IsNullOrWhiteSpace(contextSummary)
            ? ""
            : ResolveTemplate("ContextBlock", "", ("contextSummary", contextSummary));

        return ResolveTemplate("Task", "",
            ("context",    context),
            ("speaker",    speaker),
            ("transcript", transcript),
            ("date",       DateTime.Now.ToString("yyyy-MM-dd")));
    }

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
            AppendToConversationLog(date, summary);

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

    private static void AppendToConversationLog(string date, string summary)
    {
        string noteName = $"Conversations/{date}";
        string path = Path.Combine(BrainModule.VaultRoot, "Conversations", $"{date}.md");
        string existing = File.Exists(path) ? File.ReadAllText(path) : "";

        string entry = $"\n\n---\n**Coding session**\n{summary}";
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

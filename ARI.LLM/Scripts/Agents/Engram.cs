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
    protected override bool StopAfterCommit => false;

    // No work-call ceiling: the circuit breaker exists for the single-change Refactor epoch. Engram must
    // recon several existing entities (find/search/read) before it can place memories, so an 8-call cap
    // guillotines the sweep during exploration — it never reaches write_file/git_commit. Disable it here.
    protected override int? EpochToolCeiling => null;

    [JsonIgnore] internal Dialogue?    dialogue       { get; set; }
    [JsonIgnore] internal Context?     context        { get; set; }
    [JsonIgnore] internal string       PersistentDir  { get; set; } = string.Empty;

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

        // Engram now runs solely on a thread's transition to dormant (wired via Thread.BecameDormant in
        // LLMModule). The old inactivity buffer/drain is gone — it could be starved indefinitely by an
        // unanswered proactive thread sitting idle. Deletion cleanup only.
        dialogue.ThreadDeleted += threadKey =>
        {
            lastRun.Remove(threadKey);
            lastHistoryCount.Remove(threadKey);
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

        if (!IsEnabled && !force) return;
        if (force) await engramLock.WaitAsync();
        else if (!await engramLock.WaitAsync(0)) return;
        sweepingThreads[threadKey] = 0;

        // --- Run-log capture (Logs): every sweep records its full thought process for offline analysis. ---
        List<(string Title, Thread Thread)> writeThreads = new();
        List<string>                       runMeta      = new() { $"Trigger: {trigger}", $"Thread: {threadKey}" };
        string                             outcome      = "incomplete (unexpected exit)";
        bool                               processed    = false;

        try
        {
            List<ThreadItem> allItems = threads.TryGetValue(threadKey, out Thread? dialogueThread) ? dialogueThread.History : new List<ThreadItem>();
            List<ThreadItem> conversationItems = allItems.Where(i => i is Prompt or Response).ToList();

            int lastCount = lastHistoryCount.TryGetValue(threadKey, out int c) ? c : 0;
            List<ThreadItem> recentItems = conversationItems.Skip(lastCount).ToList();

            lastRun[threadKey]          = DateTime.UtcNow;
            lastHistoryCount[threadKey] = conversationItems.Count;

            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] sweep triggered (trigger: {Trigger})", threadKey, trigger);

            // --- Classify: is there anything worth remembering? ---
            runMeta.Add($"Classified transcript: {RunLogger.Trunc(BuildTranscript(recentItems), 600)}");
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
                .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "the user";

            // --- Tool-driven placement: the agent walks the graph and stores the memories itself. ---
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] placing memories via graph walk...", threadKey);
            Thread parent = new(ThreadPipeline.Dialogue, $"engram:{threadKey}:{Guid.NewGuid():N}") { Internal = true };
            RegisterTools(parent, PersistentDir, CancellationToken.None);
            PublishForInspection(parent);   // surface the sweep in the DTI
            writeThreads.Add(("Engram placement", parent));

            await SendPrompt(parent, EngramTask(transcript, contextSummary, speaker), "system",
                onDelta: async _ => { Notify?.Invoke(parent.Key); await Task.CompletedTask; });

            int commits = parent.History.OfType<Response>()
                .SelectMany(r => r.Trace ?? Enumerable.Empty<TraceStep>())
                .Count(s => s.Kind == "tool_result" && s.Name == "git_commit"
                            && (s.Text?.StartsWith("Committed", StringComparison.Ordinal) ?? false));
            outcome   = $"{commits} memory change(s) committed";
            processed = true;
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] sweep complete — {Commits} change(s).", threadKey, commits);
        }
        finally
        {
            runMeta.Add($"Outcome: {outcome}");
            RunLogger.Write("Engram", threadKey, writeThreads, runMeta);

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
            : PromptText("ContextBlock", "", ("contextSummary", contextSummary));

        return PromptText("Task", "",
            ("context",    context),
            ("speaker",    speaker),
            ("transcript", transcript),
            ("date",       DateTime.Now.ToString("yyyy-MM-dd")));
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
                new { role = "system", content = PromptText("ClassifierSystem", "") + "\n<|think_off|>" },
                new { role = "user",   content = PromptText("ClassifierTask", "", ("transcript", transcript)) }
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
}

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
    private const int ENGRAM_TRIGGER_DELAY = 5;

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

    private EngramBuffer? buffer;

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
                outcome = "skipped — classified as task-only (or no new messages)";
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
            outcome = $"{commits} memory change(s) committed";
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] sweep complete — {Commits} change(s).", threadKey, commits);
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

    // ── Placement task ─────────────────────────────────────────────────────────────────

    private static string EngramTask(string transcript, string contextSummary, string speaker)
    {
        StringBuilder sb = new();
        sb.AppendLine("A conversation just happened. Store what is worth remembering into the memory graph, then stop.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(contextSummary))
            sb.AppendLine($"CONTEXT (resolve pronouns with this):\n{contextSummary}\n");
        sb.AppendLine($"The speaker is '{speaker}'.");
        sb.AppendLine($"CONVERSATION:\n{transcript}");
        sb.AppendLine();
        sb.AppendLine("For each real person, place, event, project, pet, or thing worth remembering (NOT yourself, " +
                       "and NOT purely task/technical chatter):");
        sb.AppendLine("- Check whether it already has a note before creating one: search_brain by name (it searches " +
                       "note titles, aliases, and content, so it finds the note even under an alias), and use `neighbours` " +
                       "to see the surrounding graph. Never create a duplicate — if the same entity exists under another " +
                       "name, edit that note; if there are two, merge_notes them.");
        sb.AppendLine("- Read the relevant hub/neighbour notes (read_file) before writing, so you place it correctly " +
                       "and link it to the right owned hub. Use the path to infer where things belong.");
        sb.AppendLine("- Create or update the note with write_file / edit_file: set its type, link it outward to its hub, " +
                       "and record the new facts from this conversation.");
        sb.AppendLine($"- Maintain today's conversation log at Conversations/{DateTime.Now:yyyy-MM-dd} — a 1-3 sentence " +
                       "summary that links to everything discussed. Edit it if it already exists.");
        sb.AppendLine("- After each note (or each coherent group of edits), review with git_diff and git_commit with a " +
                       "single 'message' (first line = what changed, blank line, then why).");
        sb.AppendLine();
        sb.AppendLine("If nothing is worth storing, make no changes and stop. Preserve facts; never invent them.");
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

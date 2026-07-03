using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public enum ThreadState { Idle, Streaming, Dormant, CleanupNeeded, Deleted }

/// <summary>The pipeline a thread belongs to. Determines how its prompts are processed.</summary>
public enum ThreadPipeline { Dialogue, Code }

public class Thread
{
    private const int MIN_INACTIVITY_TIMER     = 30;
    private const int MIN_DELETION_TIMER       = 15;
    private const int MIN_INACTIVITY_THRESHOLD = 1;
    private const int DEFAULT_MEMORY_LIMIT     = 25;

    private readonly string threadKey;

    /// <summary>The pipeline this thread runs on.</summary>
    public ThreadPipeline Pipeline { get; }
    /// <summary>
    /// Whether this thread is kept off the flat user thread list (e.g. an internal worker or a
    /// Coder sub-thread). Note: "off the list" is NOT the same as "hidden" — sub-threads remain
    /// individually pollable and are surfaced live under their <see cref="Parent"/>.
    /// </summary>
    public bool Internal { get; init; }

    /// <summary>The thread that spawned this one (e.g. a CodeArchitect plan), or null for a top-level thread.</summary>
    public Thread? Parent { get; init; }

    /// <summary>True when this thread was spawned by another thread.</summary>
    public bool IsSubThread => Parent is not null;

    /// <summary>Human-readable label for a sub-thread (e.g. the atomic step it executes). Shown in the parent's live child overview.</summary>
    public string? Label { get; init; }

    private readonly List<Thread> children = new();

    /// <summary>
    /// Sub-threads this thread has spawned (e.g. per-step Coder executors under a CodeArchitect plan).
    /// Ownership + introspection only — orchestration logic lives in the agent, not here.
    /// </summary>
    public IReadOnlyList<Thread> Children
    {
        get { lock (children) { return children.ToList().AsReadOnly(); } }
    }

    internal void AddChild(Thread child)
    {
        lock (children) { children.Add(child); }
        Updated?.Invoke();
    }

    /// <summary>
    /// File names (basename) whose current content was supplied to this thread up-front (e.g. a Coder step
    /// seeded with the located range by the CodeArchitect). The Coder's "edit before read" precheck treats
    /// these as already-read, so it can edit directly from the seed without a redundant exploratory read.
    /// </summary>
    public readonly HashSet<string> PreReadPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Plan→confirm→execute gate: true while the CodeArchitect has presented a plain-English plan and
    /// is waiting for the user to approve (or adjust) it. The plan prose itself lives in History; this is just
    /// the "a plan is on the table" flag. Cleared once the approved plan is formalised into tasks and executed.</summary>
    public bool AwaitingPlanApproval;

    /// <summary>Monotonic user-turn counter, incremented by the pipeline at the start of each user request.
    /// Client-side tool guardrails (e.g. the read-dedup "already read" short-circuit) scope their state to the
    /// current turn via this: content read in an earlier turn may have been condensed out of the model's
    /// context, so "you already read this — scroll up" is only ever safe within the same turn.</summary>
    public int TurnSerial;

    /// <summary>Set by the client WebSocket layer when this thread's file tools are forwarded to a connected
    /// desktop client: registers a FRESH, independently-scoped copy of the client edit toolset onto a child
    /// thread (a spawned Coder). Sub-agents must NOT share the parent's guardrail state — a Coder starts with
    /// an empty context, so inheriting the parent's "already read" ledger blocks the very reads it needs and
    /// forces it to edit blind. Null when the project is local (ServerFileSystem is bound instead).</summary>
    public Func<Thread, bool>? ClientToolCloner;

    public readonly List<ThreadItem> History = new();

    /// <summary>The flattened display blocks for this thread — every visible <see cref="Response"/>'s blocks in
    /// order (each Response resolves its own <see cref="Subthread"/> anchors, so nesting recurses naturally).
    /// This is the DISPLAY projection a parent splices in at a <see cref="Subthread"/> anchor; it carries no
    /// thread header or timestamp, so a child renders seamlessly inside its parent's single response.</summary>
    internal List<ContentBlock> DisplayBlocks()
    {
        List<ContentBlock> blocks = new();
        foreach (ThreadItem item in History.ToArray())
            if (item is Response { IsVisible: true } r)
                blocks.AddRange(r.Blocks);
        return blocks;
    }

    internal readonly Dictionary<string, (object Schema, Func<string, Task<string>> Execute, Func<string, string>? Display, Func<string, string>? DisplayAfter, Func<string, string?>? StreamingDisplay)> tools = new();

    // ── Lifecycle ──────────────────────────────────────────────────────────────
    public ThreadState               State           = ThreadState.Idle;
    internal readonly List<TimeSpan> responseSamples = new();
    internal DateTime                ariRepliedAt    = DateTime.MinValue;
    internal Timer?                  inactivityTimer;
    internal Timer?                  dormantTimer;

    public DateTime LastMessageAt { get; internal set; } = DateTime.MinValue;

    internal TimeSpan InactivityThreshold
    {
        get
        {
            if (responseSamples.Count < 2) return TimeSpan.FromMinutes(MIN_INACTIVITY_TIMER);
            double mean     = responseSamples.Average(s => s.TotalSeconds);
            double variance = responseSamples.Average(s => Math.Pow(s.TotalSeconds - mean, 2));
            double stdDev   = Math.Sqrt(variance);
            TimeSpan adaptive = TimeSpan.FromSeconds(mean + stdDev * 2);
            TimeSpan floor    = TimeSpan.FromMinutes(MIN_INACTIVITY_THRESHOLD);
            return adaptive > floor ? adaptive : floor;
        }
    }

    internal TimeSpan DormantDuration
    {
        get
        {
            TimeSpan dormant = InactivityThreshold * 1.5;
            TimeSpan minimum = TimeSpan.FromMinutes(MIN_DELETION_TIMER);
            return dormant > minimum ? dormant : minimum;
        }
    }

    // ── Send-loop state ────────────────────────────────────────────────────────
    // These are accessed by Agent.SendPrompt / Agent.Send during request processing.
    // preserveOnCancel is also set by Pipeline.cs on cancel.
    internal readonly SemaphoreSlim sendLock         = new(1, 1);
    internal bool                   preserveOnCancel = false;

    internal volatile LiveCallInfo? liveCallInfo;
    public LiveCallInfo? LiveCall => liveCallInfo;

    internal Response? streamingResponse;
    internal string       streamedText = "";

    /// <summary>Set by the agent loop only while a tool is executing: lets a long-running tool (e.g. spawn_coder)
    /// append rendered display content into the agent's in-progress response, so a sub-agent's work shows inline
    /// and persists. Null when no tool is running.</summary>
    internal Func<string, Task>? ToolDisplaySink;

    /// <summary>The accumulated text of the response currently being generated, or null when idle.</summary>
    public string? StreamingText => streamingResponse?.StreamText;

    internal void SetLiveCall(LiveCallInfo liveCall) => liveCallInfo = liveCall;
    internal void ClearLiveCall()                    => liveCallInfo = null;

    // ── Attachments ────────────────────────────────────────────────────────────
    private readonly List<Attachment> attachments        = new();
    private readonly List<Attachment> pendingMessageAtts = new();

    internal string? PlatformContext { get; init; }
    public   string  Key             => threadKey;

    internal event Action? Updated;
    internal event Action? BufferFull;
    internal event Action<string, string>? ExchangeCompleted;
    internal event Action? BecameInactive;
    internal event Action? Deleted;
    internal event Action<string>? Streaming;
    internal event Action? StreamingFinished;

    internal void RaiseUpdated()                              => Updated?.Invoke();
    internal void RaiseBecameInactive()                       => BecameInactive?.Invoke();
    internal void RaiseExchangeCompleted(string p, string r)  => ExchangeCompleted?.Invoke(p, r);
    internal void RaiseBufferFull()                           => BufferFull?.Invoke();
    internal void RaiseStreaming(string text)                 => Streaming?.Invoke(text);
    internal void RaiseStreamingFinished()                    => StreamingFinished?.Invoke();

    // ── Constructor ─────────────────────────────────────────────────────────────

    internal Thread(ThreadPipeline pipeline, string threadKey, string? platformContext = null)
    {
        Pipeline        = pipeline;
        this.threadKey  = threadKey;
        PlatformContext = platformContext;
    }

    // ── Tools ───────────────────────────────────────────────────────────────────

    public void RegisterTool(string name, object schema, Func<string, Task<string>> executor, Func<string, string>? displayFormatter = null, Func<string, string>? displayAfterFormatter = null, Func<string, string?>? streamingDisplayFormatter = null)
        => tools[name] = (schema, executor, displayFormatter, displayAfterFormatter, streamingDisplayFormatter);

    public void UnregisterTool(string name)
        => tools.Remove(name);

    // ── History ─────────────────────────────────────────────────────────────────

    /// <summary>When the history exceeds maxChars, whole turns are evicted from the front until
    /// it fits within this fraction of the budget. Evicting past the trigger point (hysteresis)
    /// keeps the window start — and therefore the llama-server prompt prefix — stable for many
    /// turns instead of shifting on every message, which would force a full re-prefill each call.</summary>
    private const double EVICT_TO_FRACTION = 0.7;

    /// <summary>Index into History of the first item still inside the context window.
    /// Only ever moves forward, and only in whole-turn steps when a caller's char budget
    /// overflows. History is append-only apart from tail removals, so the index stays valid.</summary>
    private int contextStartIndex;

    internal List<ThreadMessage> GetChatHistory(int maxMessages = 0, int maxChars = 0)
    {
        if (contextStartIndex > History.Count) contextStartIndex = 0;

        List<(ThreadMessage Msg, int HistoryIndex, int Chars, bool EndsTurn)> kept = new();
        int charCount = 0;

        for (int i = contextStartIndex; i < History.Count; i++)
        {
            ThreadItem item = History[i];
            string? content = item.ContextText;
            if (string.IsNullOrEmpty(content)) continue;

            string author  = item.AuthorName ?? string.Empty;
            int    itemLen = author.Length + 2 + content.Length;

            charCount += itemLen;
            kept.Add((new ThreadMessage(
                Role:     author == "ARI" ? "assistant" : "user",
                Username: author,
                Content:  content), i, itemLen,
                // A turn is complete when an ariResponse closes. A Response only contributes
                // ContextText once it is Complete, so any Response seen here is a closed one.
                EndsTurn: item is Response));
        }

        if (maxChars > 0 && charCount > maxChars)
        {
            int target    = (int)(maxChars * EVICT_TO_FRACTION);
            int firstKept = 0;

            while (charCount > target)
            {
                // Advance to the item just past the next closed ariResponse — one whole turn.
                int next = firstKept;
                while (next < kept.Count && !kept[next].EndsTurn) next++;
                next++;
                if (next >= kept.Count) break; // never evict the final (possibly still-open) turn

                for (int j = firstKept; j < next; j++) charCount -= kept[j].Chars;
                firstKept = next;
            }

            if (firstKept > 0)
            {
                contextStartIndex = kept[firstKept].HistoryIndex;
                kept.RemoveRange(0, firstKept);
            }
        }

        if (maxMessages > 0 && kept.Count > maxMessages)
            kept.RemoveRange(0, kept.Count - maxMessages);

        List<ThreadMessage> result = new(kept.Count);
        foreach ((ThreadMessage msg, _, _, _) in kept) result.Add(msg);
        return result;
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────────

    internal void ResetInactivityTimer()
    {
        if (State is ThreadState.CleanupNeeded or ThreadState.Deleted) return;
        inactivityTimer?.Dispose();
        inactivityTimer = new Timer(_ =>
        {
            if (State != ThreadState.Idle) return;
            State = ThreadState.Dormant;
            BecameInactive?.Invoke();
        }, null, InactivityThreshold, Timeout.InfiniteTimeSpan);
    }

    internal void MarkEngramProcessed()
    {
        State = ThreadState.CleanupNeeded;
        Shared.Logger.LogInformation("[Thread] ({ThreadKey}) cleanup needed — scheduled for deletion in {Minutes:F1} minutes.", threadKey, DormantDuration.TotalMinutes);
        dormantTimer = new Timer(_ =>
        {
            State = ThreadState.Deleted;
            inactivityTimer?.Dispose();
            dormantTimer?.Dispose();
            Shared.Logger.LogInformation("[Thread] ({ThreadKey}) deleted.", threadKey);
            Deleted?.Invoke();
        }, null, DormantDuration, Timeout.InfiniteTimeSpan);
    }

    internal void AddItem(ThreadItem item)
    {
        History.Add(item);
        LastMessageAt = DateTime.UtcNow;
        Updated?.Invoke();
    }

    /// <summary>Removes a just-added command input when the command turned out to be unrecognised.</summary>
    internal void DropLastCommandInput()
    {
        if (History.Count > 0 && History[^1] is CommandInput)
        {
            History.RemoveAt(History.Count - 1);
            Updated?.Invoke();
        }
    }

    internal void Seed(IReadOnlyList<ThreadMessage> messages)
    {
        foreach (ThreadMessage m in messages)
        {
            if (m.Role == "assistant")
                History.Add(new Response { Content = ContentBlock.Parse(m.Content), Timestamp = DateTime.MinValue, State = global::ARI.LLM.State.Complete });
            else
                History.Add(new Prompt { AuthorName = m.Username, Text = m.Content, Timestamp = DateTime.MinValue });
        }
    }

    // ── Attachments ────────────────────────────────────────────────────────────

    public void AddAttachment(Attachment attachment)
    {
        lock (attachments) { attachments.RemoveAll(a => a.Name == attachment.Name); attachments.Add(attachment); }
    }

    public bool RemoveAttachment(string name)
    {
        lock (attachments) { return attachments.RemoveAll(a => a.Name == name) > 0; }
    }

    public IReadOnlyList<Attachment> GetAttachments()
    {
        lock (attachments) { return attachments.ToList().AsReadOnly(); }
    }

    public void AddMessageAttachment(Attachment attachment)
    {
        lock (pendingMessageAtts) { pendingMessageAtts.RemoveAll(a => a.Name == attachment.Name); pendingMessageAtts.Add(attachment); }
    }

    public bool RemoveMessageAttachment(string name)
    {
        lock (pendingMessageAtts) { return pendingMessageAtts.RemoveAll(a => a.Name == name) > 0; }
    }

    public IReadOnlyList<Attachment> GetMessageAttachments()
    {
        lock (pendingMessageAtts) { return pendingMessageAtts.ToList().AsReadOnly(); }
    }

    internal void ClearMessageAttachments()
    {
        lock (pendingMessageAtts) { pendingMessageAtts.Clear(); }
    }

    internal List<Attachment> SnapshotThreadAttachments()
    {
        lock (attachments) { return attachments.ToList(); }
    }

    internal List<Attachment> SnapshotMessageAttachments(bool fromHistory)
    {
        if (fromHistory)
        {
            Prompt? lastMsg = History.OfType<Prompt>().LastOrDefault();
            return lastMsg?.Attachments?.ToList() ?? new();
        }
        lock (pendingMessageAtts) { return pendingMessageAtts.ToList(); }
    }
}

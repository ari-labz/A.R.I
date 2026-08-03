using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>Thread lifecycle (see Docs/Thread-Lifecycle-StateMachine). A thread may only be deleted
/// from <see cref="Dormant"/>, and Engram runs on entry to Dormant — so no thread with user content is
/// ever deleted without being processed first.
/// unread → dormant (proactive, 3h) · active → streaming (generating) → active → inactive (response
/// window) → dormant (1h) → deleted (1h). Any user activity before deletion flips the thread back to
/// active. While Streaming, Ari is emitting tokens directly into the thread's live response and clients
/// fast-poll to render each token.</summary>
public enum ThreadState { Unread, Active, Streaming, Inactive, Dormant, Deleted }

/// <summary>The pipeline a thread belongs to. Determines how its prompts are processed.</summary>
public enum ThreadPipeline { Dialogue, Code, Speech }

/// <summary>Coding-pipeline state. Planning = explore/infer/propose (no edits); Development = execute the
/// approved plan (no exploration). Each phase feeds the agent a different system prompt and sampling.</summary>
public enum CodePhase { Planning, Development }

public class Thread
{
    // Lifecycle timing (see Docs/Thread-Lifecycle-StateMachine). Kept as named constants for now;
    // promote to config if these need tuning at runtime.
    private const int RESPONSE_WINDOW_FLOOR_MIN  = 5;   // active→inactive floor + no-data default (minutes)
    private const int RESPONSE_WINDOW_BUFFER_MIN = 5;   // safety buffer added to the average response time
    private const int UNREAD_GRACE_HOURS         = 3;   // proactive: wait this long for a first reply
    private const int INACTIVE_TO_DORMANT_MIN    = 60;  // inactive → dormant
    private const int DORMANT_TO_DELETE_MIN      = 60;  // dormant  → deleted
    private const int DELETE_RETRY_SEC           = 30;  // re-check cadence when Engram hasn't finished at delete time

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

    /// <summary>Short (3-4 word) auto-generated title reflecting the conversation, refreshed by the Context agent each exchange. Null until the first update.</summary>
    public string? Title { get; set; }

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

    /// <summary>Coding-pipeline state machine. Planning: explore/infer/propose, no edit tools. Development:
    /// execute the approved plan, no exploration. The phase selects the agent's system prompt AND sampling
    /// per turn (see Agent.Phases), and the Planning→Development transition is the context-pruning point.
    /// Entry state is Planning; the model moves it via dev_mode / planning_mode after presenting a plan.</summary>
    public CodePhase Phase = CodePhase.Planning;

    /// <summary>The self-contained handoff the architect writes when it calls dev_mode: the plan + the exact
    /// file contracts Development needs (fields, signatures, patterns). Tool-result cards do NOT persist across
    /// turns (only prose does), so the working set would vanish at the Planning→Development boundary and force
    /// re-reads — this payload carries it forward as text. Injected into every Development turn's prompt.</summary>
    public string? HandoffPayload;

    /// <summary>A plan is on the table awaiting the user's verdict (set by plan_proposed, which force-ends the
    /// planning turn). On the next turn CodePipeline reads the user's verdict: approve → Development (with the
    /// captured HandoffPayload); anything else → back to Planning to revise.</summary>
    public bool PlanProposed;

    /// <summary>This planning turn is a REVISION of a plan the user just amended (set by CodePipeline when a
    /// proposed plan gets feedback instead of approval). CodeArchitect uses it to hard-steer the turn to end
    /// with a fresh plan_proposed.</summary>
    public bool RevisingPlan;

    /// <summary>Set by plan_proposed / replan to force the current turn to end after this tool batch (a clean
    /// phase boundary), read by CodeArchitect.ShouldBreak.</summary>
    public bool EndTurnNow;

    /// <summary>Files the architect has edited/written this turn (populated by the edit tools via OnToolResult).
    /// build_project builds the projects containing these. Replaces the old spawn_coder-populated set now that
    /// the architect edits directly instead of dispatching a Coder.</summary>
    public readonly HashSet<string> TouchedFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The root filesystem path this thread is bound to (a project root, or the brain vault) —
    /// set once per turn by whichever agent knows it (Coder.RunLoop, MemoryAgent.RegisterTools). Null
    /// means no project is bound. ToolFactories reads this to construct project-scoped tools (git,
    /// filesystem, build) for ANY agent's request_tools call — availability depends on this state, not on
    /// which agent is asking. There is no per-agent tool allowlist; a group resolves or it doesn't based
    /// on what's actually bound here.</summary>
    internal string? ProjectRoot   { get; set; }
    internal FileSnapshots? Snapshots     { get; set; }
    internal bool     IsBrainVault { get; set; }
    internal bool     IsRemoteProject { get; set; }
    internal CancellationToken Ct  { get; set; }

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

    internal readonly Dictionary<string, (object Schema, Func<string, Task<string>> Execute, Func<string, string>? Display, Func<string, string>? DisplayAfter, Func<string, string?>? StreamingDisplay, Func<string, string?>? StreamingPreCheck, Func<string, string?>? PreCheck)> tools = new();

    // ── Lifecycle ──────────────────────────────────────────────────────────────
    public ThreadState               State           = ThreadState.Active;
    internal readonly List<TimeSpan> responseSamples = new();
    internal DateTime                ariRepliedAt    = DateTime.MinValue;
    internal Timer?                  inactivityTimer;
    internal Timer?                  dormantTimer;

    public DateTime LastMessageAt { get; internal set; } = DateTime.MinValue;

    /// <summary>Proof that Engram has processed the conversation in its current state. Set true when a
    /// sweep completes (or there is nothing to save); reset to false whenever new user input arrives.
    /// A thread may only advance from Dormant to Deleted while this is true.</summary>
    public bool EngramProcessed { get; internal set; }

    /// <summary>True once the user has said anything in this thread — the gate for whether a dormant
    /// sweep has anything to learn. An unanswered proactive thread has none.</summary>
    internal bool HasUserMessages => History.OfType<Prompt>().Any();

    /// <summary>The active→inactive countdown: the average of this thread's response times plus a fixed
    /// safety buffer, floored at (and defaulting, with no samples yet, to) the floor.</summary>
    internal TimeSpan ResponseWindow
    {
        get
        {
            TimeSpan floor = TimeSpan.FromMinutes(RESPONSE_WINDOW_FLOOR_MIN);
            if (responseSamples.Count == 0) return floor;
            double avgSec   = responseSamples.Average(s => s.TotalSeconds);
            TimeSpan window = TimeSpan.FromSeconds(avgSec) + TimeSpan.FromMinutes(RESPONSE_WINDOW_BUFFER_MIN);
            return window > floor ? window : floor;
        }
    }

    // ── Send-loop state ────────────────────────────────────────────────────────
    // These are accessed by Agent.Prompt / Agent.Send during request processing.
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
    /// <summary>Fires on entry to Dormant — the Engram trigger point.</summary>
    internal event Action? BecameDormant;
    internal event Action? Deleted;
    internal event Action<string>? Streaming;
    internal event Action? StreamingFinished;

    internal void RaiseUpdated()                              => Updated?.Invoke();
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

    public void RegisterTool(string name, object schema, Func<string, Task<string>> executor, Func<string, string>? displayFormatter = null, Func<string, string>? displayAfterFormatter = null, Func<string, string?>? streamingDisplayFormatter = null, Func<string, string?>? streamingPreCheck = null, Func<string, string?>? preCheck = null)
        => tools[name] = (schema, executor, displayFormatter, displayAfterFormatter, streamingDisplayFormatter, streamingPreCheck, preCheck);

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

        // Message-count trim with the SAME whole-turn hysteresis as the char eviction above. A plain
        // "remove down to exactly maxMessages" slides the window start one message every turn once the
        // limit is reached, which shifts the prompt prefix every turn and forces a full re-prefill each
        // time (measured: reuse collapsed 95%→5% and prefill spiked 2.5s→34s the turn a conversation
        // crossed 25 messages). Instead: only trigger past maxMessages, then evict whole turns down to
        // EVICT_TO_FRACTION of the limit and advance contextStartIndex, so the window start stays put
        // for several turns (re-prefill once every ~4 turns, not every turn).
        if (maxMessages > 0 && kept.Count > maxMessages)
        {
            int target    = Math.Max(1, (int)(maxMessages * EVICT_TO_FRACTION));
            int firstKept = 0;

            while (kept.Count - firstKept > target)
            {
                int next = firstKept;
                while (next < kept.Count && !kept[next].EndsTurn) next++;
                next++;
                if (next >= kept.Count) break;   // never evict the final (possibly still-open) turn

                firstKept = next;
            }

            if (firstKept > 0)
            {
                contextStartIndex = kept[firstKept].HistoryIndex;
                kept.RemoveRange(0, firstKept);
            }
        }

        List<ThreadMessage> result = new(kept.Count);
        foreach ((ThreadMessage msg, _, _, _) in kept) result.Add(msg);
        return result;
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────────

    // State machine: active ─window─▶ inactive ─1h─▶ dormant ─1h(+Engram)─▶ deleted; unread ─3h─▶ dormant.
    // Timers are single-shot; each transition disposes the previous and arms the next. inactivityTimer
    // carries the active→inactive (and unread→dormant) countdown; dormantTimer carries inactive→dormant,
    // dormant→delete, and the delete-retry poll.

    private void DisposeTimers()
    {
        inactivityTimer?.Dispose(); inactivityTimer = null;
        dormantTimer?.Dispose();    dormantTimer    = null;
    }

    /// <summary>A real user message is being processed: cancel any pending deletion, enter Streaming (Ari
    /// is about to generate), and mark the conversation as needing (re)processing. No window is armed —
    /// the response is in flight and <see cref="OnResponseComplete"/> arms it when generation ends.</summary>
    internal void OnUserSend()
    {
        if (State == ThreadState.Deleted) return;
        DisposeTimers();
        State           = ThreadState.Streaming;
        EngramProcessed = false;
    }

    /// <summary>Generation ended abnormally (cancel/error) before <see cref="OnResponseComplete"/> ran.
    /// Return to active and arm the response window so the thread doesn't strand in Streaming.</summary>
    internal void OnGenerationAborted()
    {
        if (State != ThreadState.Streaming) return;
        State = ThreadState.Active;
        ArmResponseWindow();
    }

    /// <summary>The user is composing (typing indicator): keep the thread alive and re-arm the response
    /// window so it does not drift to inactive/deletion mid-compose.</summary>
    internal void OnUserTyping()
    {
        if (State == ThreadState.Deleted) return;
        DisposeTimers();
        State = ThreadState.Active;
        ArmResponseWindow();
    }

    /// <summary>Ari has finished a response — start the response-window countdown toward inactive.</summary>
    internal void OnResponseComplete()
    {
        if (State == ThreadState.Deleted) return;
        ariRepliedAt = DateTime.UtcNow;
        State        = ThreadState.Active;
        ArmResponseWindow();
    }

    /// <summary>Proactive opener sent: await the user's first reply for the unread grace period, then go
    /// dormant (Engram is skipped there since there are no user messages).</summary>
    internal void StartUnread()
    {
        if (State == ThreadState.Deleted) return;
        DisposeTimers();
        State = ThreadState.Unread;
        Shared.Logger.LogInformation("[Thread] ({ThreadKey}) unread — awaiting reply for {Hours}h.", threadKey, UNREAD_GRACE_HOURS);
        inactivityTimer = new Timer(_ => ToDormant(), null, TimeSpan.FromHours(UNREAD_GRACE_HOURS), Timeout.InfiniteTimeSpan);
    }

    private void ArmResponseWindow()
    {
        inactivityTimer?.Dispose();
        inactivityTimer = new Timer(_ => ToInactive(), null, ResponseWindow, Timeout.InfiniteTimeSpan);
    }

    private void ToInactive()
    {
        if (State != ThreadState.Active) return;
        State = ThreadState.Inactive;
        Shared.Logger.LogInformation("[Thread] ({ThreadKey}) inactive — dormant in {Min}m.", threadKey, INACTIVE_TO_DORMANT_MIN);
        BecameInactive?.Invoke();
        dormantTimer?.Dispose();
        dormantTimer = new Timer(_ => ToDormant(), null, TimeSpan.FromMinutes(INACTIVE_TO_DORMANT_MIN), Timeout.InfiniteTimeSpan);
    }

    private void ToDormant()
    {
        if (State is not (ThreadState.Inactive or ThreadState.Unread)) return;
        State = ThreadState.Dormant;
        Shared.Logger.LogInformation("[Thread] ({ThreadKey}) dormant — running Engram; deletion in {Min}m.", threadKey, DORMANT_TO_DELETE_MIN);
        BecameDormant?.Invoke();   // Engram runs here; handler marks EngramProcessed when there's nothing to save
        dormantTimer?.Dispose();
        dormantTimer = new Timer(_ => Delete(), null, TimeSpan.FromMinutes(DORMANT_TO_DELETE_MIN), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Delete the thread — but NEVER before Engram has processed it. This is the single, universal
    /// delete gate: whoever calls it (the dormant timer, a manual close, any direct caller), if the thread
    /// still has unprocessed user content it is moved to Dormant, Engram is (re)triggered, and actual
    /// removal is deferred — this method re-arms itself until <see cref="EngramProcessed"/> is set. Fires
    /// <see cref="Deleted"/> (registry removal + client broadcast) only once the sweep has completed.</summary>
    internal void Delete()
    {
        if (State == ThreadState.Deleted) return;

        if (!EngramProcessed && HasUserMessages)
        {
            if (State != ThreadState.Dormant) State = ThreadState.Dormant;
            Shared.Logger.LogInformation("[Thread] ({ThreadKey}) delete gated — running Engram first; retrying in {Sec}s.", threadKey, DELETE_RETRY_SEC);
            BecameDormant?.Invoke();   // (re)trigger the sweep; handler no-ops if one is already running
            dormantTimer?.Dispose();
            dormantTimer = new Timer(_ => Delete(), null, TimeSpan.FromSeconds(DELETE_RETRY_SEC), Timeout.InfiniteTimeSpan);
            return;
        }

        State = ThreadState.Deleted;
        DisposeTimers();
        Shared.Logger.LogInformation("[Thread] ({ThreadKey}) deleted.", threadKey);
        Deleted?.Invoke();
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

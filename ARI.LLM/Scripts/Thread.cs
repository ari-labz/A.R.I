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
    /// <summary>Whether this is an internal working thread, hidden from the user.</summary>
    public bool Internal { get; init; }

    public readonly List<ThreadItem> History = new();

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

    internal AriResponse? streamingResponse;
    internal string       streamedText = "";

    /// <summary>The accumulated text of the response currently being generated, or null when idle.</summary>
    public string? StreamingText => streamingResponse?.StreamText;

    internal void SetLiveCall(LiveCallInfo liveCall) => liveCallInfo = liveCall;
    internal void ClearLiveCall()                    => liveCallInfo = null;

    // ── Attachments ────────────────────────────────────────────────────────────
    private readonly List<Attachment> attachments        = new();
    private readonly List<Attachment> pendingMessageAtts = new();

    internal string? PlatformContext { get; init; }
    internal string  Key             => threadKey;

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

    internal List<ThreadMessage> GetChatHistory(int maxMessages = 0, int maxChars = 0)
    {
        List<ThreadMessage> result = new();
        int charCount = 0;

        for (int i = History.Count - 1; i >= 0; i--)
        {
            if (maxMessages > 0 && result.Count >= maxMessages) break;

            ThreadItem item = History[i];
            string? content = item.ContextText;
            if (string.IsNullOrEmpty(content)) continue;

            int itemLen = item.AuthorName.Length + 2 + content.Length;
            if (maxChars > 0 && charCount + itemLen > maxChars) break;

            charCount += itemLen;
            result.Add(new ThreadMessage(
                Role:     item.AuthorName == "ARI" ? "assistant" : "user",
                Username: item.AuthorName,
                Content:  content));
        }

        result.Reverse();
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
                History.Add(new AriResponse { Content = AriContentBlock.Parse(m.Content), Timestamp = DateTime.MinValue, State = AriResponseState.Complete });
            else
                History.Add(new UserMessage { Username = m.Username, Content = m.Content, Timestamp = DateTime.MinValue });
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
            UserMessage? lastMsg = History.OfType<UserMessage>().LastOrDefault();
            return lastMsg?.Attachments?.ToList() ?? new();
        }
        lock (pendingMessageAtts) { return pendingMessageAtts.ToList(); }
    }
}

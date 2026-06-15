using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public enum ThreadState { Active, Inactive, Dormant, Deleted }

/// <summary>The pipeline a thread belongs to. Determines how its prompts are processed.</summary>
public enum ThreadType { Dialogue, Code, Memory, Engram, Context, Refactor, Classifier }

public class Thread
{
    private const int     MIN_INACTIVITY_TIMER     = 30;
    private const int     MIN_DELETION_TIMER       = 15;
    private const int     MIN_INACTIVITY_THRESHOLD = 1;
    private const int     DEFAULT_MEMORY_LIMIT     = 25;
    private const int     CHARS_PER_TOKEN          = 4;
    private const double  TEMPERATURE              = 0.7;   // Qwen3 recommendation for non-thinking mode
    private const double  TOP_P                    = 0.95;  // Qwen3 recommendation
    private const int     TOP_K                    = 20;    // Qwen3 recommendation; tighter tail = steadier structured output
    private const double  MIN_P                    = 0.05;  // cut the low-probability tail that derails tool-call JSON
    private const double  REPEAT_PENALTY           = 1.0;
    private const double  TOKEN_WARNING_RATIO      = 0.8;
    private const double  COMPACT_RATIO            = 0.6;  // compact tool output once context exceeds this fraction of the window
    private const int     COMPACT_KEEP_RECENT      = 3;    // most-recent tool results always kept full
    private const int     MAX_DEGRADE_EVENTS       = 5;    // tool-format failures per turn before aborting to avoid a spiral
    private const string  ATTACHMENT_DIVIDER       = "-------------------";

    private readonly string     threadKey;
    private readonly HttpClient httpClient;

    /// <summary>The pipeline this thread runs on.</summary>
    public ThreadType Type { get; }
    /// <summary>Whether this is an internal working thread, hidden from the user.</summary>
    public bool Internal => Type is not (ThreadType.Dialogue or ThreadType.Code);

    public readonly List<ThreadItem> History = new();

    private readonly Dictionary<string, (object Schema, Func<string, Task<string>> Execute, Func<string, string>? Display, Func<string, string>? DisplayAfter, Func<string, string?>? StreamingDisplay)> tools = new();

    // ── Lifecycle ──────────────────────────────────────────────────────────────
    public ThreadState              State           = ThreadState.Active;
    private readonly List<TimeSpan> responseSamples = new();
    private DateTime                ariRepliedAt    = DateTime.MinValue;
    private Timer?                  inactivityTimer;
    private Timer?                  dormantTimer;

    public DateTime    LastMessageAt { get; private set; } = DateTime.MinValue;

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

    private readonly SemaphoreSlim sendLock         = new(1, 1);
    internal bool                  preserveOnCancel = false;

    private volatile LiveCallInfo? liveCallInfo;
    public LiveCallInfo? LiveCall => liveCallInfo;

    // The response currently being streamed into History, so cancel/error handling can finalise it.
    private AriResponse? streamingResponse;
    private string       streamedText = "";

    internal void SetLiveCall(LiveCallInfo liveCall) => liveCallInfo = liveCall;

    // ── Attachments ────────────────────────────────────────────────────────────
    private readonly List<Attachment> attachments        = new();
    private readonly List<Attachment> pendingMessageAtts = new();

    internal string? PlatformContext { get; init; }

    // ── Persistent context ───────────────────────────────────────────────────────
    // Always-on blocks injected into the system message every Send, separate from the
    // sliding chat-history window. Set externally (conventions / project rules / map) or
    // maintained internally (the task checklist). Null/empty blocks are skipped.
    public string? CodingConventions { get; set; }
    public string? ProjectRules      { get; set; }
    public string? ProjectMap        { get; set; }

    public readonly record struct TodoItem(string Content, string Status);
    private readonly List<TodoItem> todos = new();
    public IReadOnlyList<TodoItem> Todos => todos;

    internal event Action? Updated;
    internal event Action? BufferFull;
    internal event Action<string, string>? ExchangeCompleted;
    internal event Action? BecameInactive;
    internal event Action? Deleted;

    // ── Constructor ─────────────────────────────────────────────────────────────

    internal Thread(ThreadType type, string threadKey, string? platformContext = null)
    {
        Type            = type;
        this.threadKey  = threadKey;
        httpClient      = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        PlatformContext = platformContext;
    }

    // ── Tools ───────────────────────────────────────────────────────────────────

    public void RegisterTool(string name, object schema, Func<string, Task<string>> executor, Func<string, string>? displayFormatter = null, Func<string, string>? displayAfterFormatter = null, Func<string, string?>? streamingDisplayFormatter = null)
        => tools[name] = (schema, executor, displayFormatter, displayAfterFormatter, streamingDisplayFormatter);

    public void UnregisterTool(string name)
        => tools.Remove(name);

    /// <summary>Registers the in-process task-checklist tool on this thread. Exposed so the API layer
    /// can wire it up without referencing the internal tool type.</summary>
    public void RegisterTodosTool()
        => new UpdateTodos(this).Register(this);

    // ── Persistent context ───────────────────────────────────────────────────────

    /// <summary>Assembles the static always-on blocks (conventions, project rules, map). The live
    /// task checklist is injected separately each loop iteration since it changes mid-turn.</summary>
    internal string BuildStaticContext()
    {
        StringBuilder sb = new();
        void Block(string title, string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            sb.Append("\n\n").Append(title).Append('\n').Append(body.Trim());
        }
        Block("## Coding conventions", CodingConventions);
        Block("## Project rules",      ProjectRules);
        Block("## Project map",        ProjectMap);
        return sb.ToString();
    }

    /// <summary>Renders the current checklist as a markdown block, or empty when there are none.</summary>
    internal string RenderTodoBlock()
    {
        if (todos.Count == 0) return "";
        StringBuilder sb = new("\n\n## Task checklist (keep current with update_todos)\n");
        foreach (TodoItem t in todos)
        {
            string box = t.Status switch { "completed" => "[x]", "in_progress" => "[~]", _ => "[ ]" };
            sb.Append(box).Append(' ').Append(t.Content).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Replaces the whole checklist from an update_todos tool-call payload. Returns a
    /// short confirmation for the model. Runs in-process (never round-trips to the client).</summary>
    internal string ReplaceTodos(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            todos.Clear();
            if (doc.RootElement.TryGetProperty("todos", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in arr.EnumerateArray())
                {
                    string content = el.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
                    string status  = el.TryGetProperty("status",  out JsonElement s) ? s.GetString() ?? "pending" : "pending";
                    if (status is not ("pending" or "in_progress" or "completed")) status = "pending";
                    if (!string.IsNullOrWhiteSpace(content)) todos.Add(new TodoItem(content.Trim(), status));
                }
            }
            Updated?.Invoke();
            int done = todos.Count(t => t.Status == "completed");
            string body = RenderTodoBlock();
            return $"Checklist updated — {done}/{todos.Count} complete.{(body.Length > 0 ? "\n" + body : "")}";
        }
        catch (Exception ex) { return $"Error updating checklist: {ex.Message}"; }
    }

    /// <summary>Count of checklist items not yet completed (used by the finish-time reminder).</summary>
    internal int IncompleteTodoCount() => todos.Count(t => t.Status != "completed");

    // ── History ─────────────────────────────────────────────────────────────────

    internal List<ThreadMessage> GetChatHistory(int maxMessages = 0, int maxChars = 0)
    {
        List<ThreadMessage> result = new();
        int charCount = 0;

        for (int i = History.Count - 1; i >= 0; i--)
        {
            if (maxMessages > 0 && result.Count >= maxMessages) break;

            ThreadItem item = History[i];
            // ContextText is the marker-stripped projection sent to the model; for most items
            // it is identical to Message, but AriResponse strips its UI-only tool-use markup.
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
        if (State != ThreadState.Active) return;
        inactivityTimer?.Dispose();
        inactivityTimer = new Timer(_ =>
        {
            if (State != ThreadState.Active) return;
            State = ThreadState.Inactive;
            BecameInactive?.Invoke();
        }, null, InactivityThreshold, Timeout.InfiniteTimeSpan);
    }

    internal void MarkEngramProcessed()
    {
        State = ThreadState.Dormant;
        Common.Logger.LogInformation("[Thread] ({ThreadKey}) dormant — scheduled for deletion in {Minutes:F1} minutes.", threadKey, DormantDuration.TotalMinutes);
        dormantTimer = new Timer(_ =>
        {
            State = ThreadState.Deleted;
            inactivityTimer?.Dispose();
            dormantTimer?.Dispose();
            Common.Logger.LogInformation("[Thread] ({ThreadKey}) deleted.", threadKey);
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

    // ── SendPrompt ──────────────────────────────────────────────────────────────

    internal async Task<string> SendPrompt(
        Agent               agent,
        string              prompt,
        string              username               = "user",
        string?             augmentedPrompt        = null,
        string?             recallNotes            = null,
        string?             contextSummary         = null,
        int                 maxTokensOverride      = 0,
        CancellationToken   ct                     = default,
        bool                userMessagePreadded    = false,
        Func<string, Task>? onDelta                = null,
        int                 thinkingBudgetOverride = 0)
    {
        await sendLock.WaitAsync(ct);
        try
        {
            return await Send(agent, prompt, username, augmentedPrompt, recallNotes, contextSummary, maxTokensOverride, ct, userMessagePreadded, onDelta, thinkingBudgetOverride);
        }
        catch (OperationCanceledException)
        {
            liveCallInfo = null;
            if (!preserveOnCancel)
            {
                // Explicit cancel: revert as if the prompt never happened.
                if (streamingResponse is not null) History.Remove(streamingResponse);
                if (History.Count > 0 && History[^1] is UserMessage) History.RemoveAt(History.Count - 1);
            }
            else if (streamingResponse is not null)
            {
                // Superseded by a new prompt: keep the partial in the list, hidden, out of context.
                streamingResponse.Content = AriContentBlock.Parse(streamedText);
                streamingResponse.State   = AriResponseState.Cancelled;
            }
            preserveOnCancel  = false;
            streamingResponse = null;
            throw;
        }
        catch (Exception ex)
        {
            // Any other failure: show an error bubble so the user always gets feedback.
            // If something was already streamed, keep that partial text; otherwise replace the
            // empty bubble with the error message rather than silently removing it.
            if (streamingResponse is not null)
            {
                streamingResponse.Content = AriContentBlock.Parse(
                    string.IsNullOrWhiteSpace(streamedText)
                        ? $"[Error: {ex.Message}]"
                        : streamedText);
                streamingResponse.State = AriResponseState.Error;
                Updated?.Invoke();
            }
            streamingResponse = null;
            throw;
        }
        finally
        {
            liveCallInfo = null;
            sendLock.Release();
        }
    }

    private async Task<string> Send(
        Agent               agent,
        string              prompt,
        string              username,
        string?             augmentedPrompt,
        string?             recallNotes,
        string?             contextSummary,
        int                 maxTokensOverride,
        CancellationToken   ct,
        bool                userMessagePreadded,
        Func<string, Task>? onDelta               = null,
        int                 thinkingBudgetOverride = 0)
    {
        LastMessageAt = DateTime.UtcNow;

        // Always kill the inactivity timer when a new exchange begins — regardless of state.
        // Without this, the timer from the previous completed exchange can fire mid-generation
        // and trigger BecameInactive while the AriResponse is still open.
        inactivityTimer?.Dispose();
        inactivityTimer = null;

        if (State != ThreadState.Active)
        {
            dormantTimer?.Dispose();
            dormantTimer = null;
            State = ThreadState.Active;
        }

        if (ariRepliedAt != DateTime.MinValue)
        {
            int sampleWindow = agent.MemoryLimit > 0 ? agent.MemoryLimit : DEFAULT_MEMORY_LIMIT;
            responseSamples.Add(DateTime.UtcNow - ariRepliedAt);
            if (responseSamples.Count > sampleWindow)
                responseSamples.RemoveAt(0);
            ariRepliedAt = DateTime.MinValue;
        }

        List<Attachment> threadAtts;
        List<Attachment> msgAtts;
        lock (attachments) { threadAtts = attachments.ToList(); }
        if (userMessagePreadded)
        {
            UserMessage? lastMsg = History.OfType<UserMessage>().LastOrDefault();
            msgAtts = lastMsg?.Attachments?.ToList() ?? new();
        }
        else
        {
            lock (pendingMessageAtts) { msgAtts = pendingMessageAtts.ToList(); }
        }

        if (!userMessagePreadded)
        {
            History.Add(new UserMessage
            {
                Username    = username,
                Content     = prompt,
                Timestamp   = DateTime.Now,
                Attachments = msgAtts.Count > 0 ? msgAtts.ToList() : null
            });
            Updated?.Invoke();
        }

        int maxChars = agent.MaxContextTokens > 0 ? agent.MaxContextTokens * 2 : 0;
        List<ThreadMessage> chatHistory = GetChatHistory(agent.MemoryLimit, maxChars);

        List<ThreadMessage> collapsed = new();
        foreach (ThreadMessage m in chatHistory)
        {
            if (collapsed.Count > 0 && collapsed[^1].Role == m.Role)
                collapsed[^1] = collapsed[^1] with { Content = collapsed[^1].Content + "\n" + m.Content };
            else
                collapsed.Add(m);
        }

        if (augmentedPrompt is not null && collapsed.Count > 0)
            collapsed[^1] = collapsed[^1] with { Content = augmentedPrompt };

        // Base (static) system content: agent prompt + platform context + always-on persistent
        // blocks (conventions / project rules / map). The live task checklist is appended per
        // iteration inside the loop because it changes mid-turn.
        string baseSystem = PlatformContext is null
            ? agent.SystemPrompt
            : $"{agent.SystemPrompt}\n\n{PlatformContext}";
        baseSystem += BuildStaticContext();
        string thinkSuffix = agent.Think ? "" : "\n<|think_off|>";

        List<object> messages = new List<object> { new { role = "system", content = baseSystem + thinkSuffix } };

        for (int i = 0; i < collapsed.Count - 1; i++)
        {
            ThreadMessage m = collapsed[i];
            messages.Add(new { role = m.Role, content = $"{m.Username}: {m.Content}" });
        }

        if (collapsed.Count > 0)
        {
            ThreadMessage current  = collapsed[^1];
            string        promptText = $"{current.Username}: {current.Content}";

            List<Attachment> threadImages = threadAtts.Where(a =>  a.IsImage).ToList();
            List<Attachment> threadTexts  = threadAtts.Where(a => !a.IsImage).ToList();
            List<Attachment> msgImages    = msgAtts.Where(a =>  a.IsImage).ToList();
            List<Attachment> msgTexts     = msgAtts.Where(a => !a.IsImage).ToList();

            bool hasThreadContent = threadImages.Count > 0 || threadTexts.Count > 0;
            bool hasMsgContent    = msgImages.Count > 0    || msgTexts.Count > 0;

            if (!hasThreadContent && !hasMsgContent)
            {
                messages.Add(new { role = "user", content = promptText });
            }
            else
            {
                List<object> contentParts = new();

                bool hasTools = tools.Count > 0;

                if (hasThreadContent)
                {
                    StringBuilder sb = new();
                    sb.AppendLine("[Files attached to this thread]");
                    foreach (Attachment a in threadTexts)
                    {
                        sb.AppendLine($"--- {a.Name} ---");
                        sb.AppendLine(a.Content);
                        sb.AppendLine("---");
                    }
                    if (threadTexts.Count > 0)
                    {
                        if (hasTools) sb.AppendLine("(The above files are already provided inline — do not call read_file for them.)");
                        sb.AppendLine(ATTACHMENT_DIVIDER);
                    }
                    contentParts.Add(new { type = "text", text = sb.ToString().TrimEnd() });

                    foreach (Attachment a in threadImages)
                        contentParts.Add(new { type = "image_url", image_url = new { url = $"data:{a.MimeType ?? "image/jpeg"};base64,{a.Content}" } });
                }

                if (hasMsgContent)
                {
                    StringBuilder sb = new();
                    sb.AppendLine("[Files attached to this message]");
                    foreach (Attachment a in msgTexts)
                    {
                        sb.AppendLine($"--- {a.Name} ---");
                        sb.AppendLine(a.Content);
                        sb.AppendLine("---");
                    }
                    if (msgTexts.Count > 0)
                    {
                        if (hasTools) sb.AppendLine("(The above files are already provided inline — do not call read_file for them.)");
                        sb.AppendLine(ATTACHMENT_DIVIDER);
                    }
                    contentParts.Add(new { type = "text", text = sb.ToString().TrimEnd() });

                    foreach (Attachment a in msgImages)
                        contentParts.Add(new { type = "image_url", image_url = new { url = $"data:{a.MimeType ?? "image/jpeg"};base64,{a.Content}" } });
                }

                contentParts.Add(new { type = "text", text = promptText });
                messages.Add(new { role = "user", content = (object)contentParts });
            }
        }

        if (!agent.QuietLogging && !agent.SuppressPromptLog)
            Common.Logger.LogInformation("[{Agent}] ({Thread}) prompt\n\"{Prompt}\"", agent.Name, threadKey, prompt);

        int             maxTokens        = maxTokensOverride != 0 ? maxTokensOverride : agent.MaxTokens;
        int             toolCallCount    = 0;
        int             parseFailures        = 0;
        int             consecutiveFallbacks = 0;
        List<string>    toolResults      = new();
        // Soft re-read guard: counts how many times each file has been read this turn.
        // Capped at 3 to prevent spiral loops; model is told to proceed rather than keep re-reading.
        Dictionary<string, int> readCounts  = new(StringComparer.OrdinalIgnoreCase);
        // Tracks files edited this turn so stale old_string errors include a re-read hint.
        HashSet<string>         editedFiles = new(StringComparer.OrdinalIgnoreCase);
        // Counts consecutive failed edit attempts per file this turn, to escalate guidance and
        // ultimately cut off tool access rather than letting the model spiral on the same edit.
        Dictionary<string, int> editFailStreak = new(StringComparer.OrdinalIgnoreCase);
        // Counts write_file calls per file this turn. Rewriting the same file repeatedly is a
        // spiral (the model distrusts its own successful write and regenerates the whole file).
        Dictionary<string, int> writeCounts = new(StringComparer.OrdinalIgnoreCase);
        bool                    forceNoMoreTools = false;
        // Files edited in the CURRENT batch of tool calls (cleared each iteration). A second edit
        // to the same file within one batch is rejected because its old_string was written against
        // pre-edit content and would fail or corrupt the file.
        HashSet<string>         editedPathsThisBatch = new(StringComparer.OrdinalIgnoreCase);
        // Caps the finish-time "you have incomplete checklist items" reminders so we can't loop forever.
        int                     todoReminders = 0;
        // Distinct files the model has edited/written this turn, and a one-shot flag for the
        // checklist nudge: the moment a SECOND distinct file is touched with no checklist, we force
        // the model to call update_todos first. Single-file tasks never trip it.
        HashSet<string>         turnEditPaths = new(StringComparer.OrdinalIgnoreCase);
        bool                    todoNudged = false;
        // The model spells the same file inconsistently across calls ("X.cs" vs "Dir/X.cs"), which
        // splits the per-file guard counters so the spiral cutoff never fires. Key everything by
        // filename so failures on one file always accumulate to the same counter.
        static string NormKey(string p) => System.IO.Path.GetFileName(p.Trim('"', '\'', ' ', '\\'));
        // Slots (message index + id + name) of every real tool-result message this turn, oldest first,
        // so context compaction can stub the oldest outputs when the window fills.
        List<(int Index, string CallId, string Name)> toolResultSlots = new();
        // Cumulative tool-format failures this turn (fallbacks, arg repairs, parse-failure 500s).
        // Unlike consecutiveFallbacks this never resets, so a spiral of interspersed failures still
        // trips the backstop. Degrade() increments it and aborts cleanly past MAX_DEGRADE_EVENTS.
        int                     degradeEvents = 0;
        void Degrade()
        {
            if (++degradeEvents >= MAX_DEGRADE_EVENTS)
                throw new LlmRequestFailedException(
                    $"Tool-call formatting failed {degradeEvents} times this turn — stopping to avoid a spiral. Any changes already applied are kept.");
        }
        // Context hygiene: tracks the messages-array slot holding the most recent read_file result
        // for each file this turn, so an earlier copy can be stubbed when the file is re-read or
        // changed. Keeps exactly one live copy of any file and drops outdated snapshots entirely.
        Dictionary<string, (int Index, string CallId)> liveReads = new(StringComparer.OrdinalIgnoreCase);
        StringBuilder   responseBuilder  = new();
        StringBuilder   contentBuilder   = new(); // accumulates text + tool indicators across all iterations
        Stopwatch       sw               = Stopwatch.StartNew();
        bool            wasThinking      = false;
        int             completionTokens = 0;
        int             promptTokens     = 0;
        bool            hadImages        = msgAtts.Any(a => a.IsImage) || threadAtts.Any(a => a.IsImage);

        int estimatedTextTokens = messages.Sum(m =>
        {
            string? content = m.GetType().GetProperty("content")?.GetValue(m) as string;
            return (content?.Length ?? 0) / CHARS_PER_TOKEN;
        });

        if (liveCallInfo is { } existing)
        {
            existing.EstimatedInputTokens = estimatedTextTokens;
            existing.OutputTokenLimit     = maxTokens;
            existing.HadImages            = hadImages;
        }
        else
        {
            liveCallInfo = new LiveCallInfo(agent.Name, threadKey, estimatedTextTokens, maxTokens, agent.MaxContextTokens, agent.MaxImageTokens, hadImages);
        }

        // The response lives in History from the moment generation starts, so partial output
        // survives an error or interruption. The model streams into it via the wrapped onDelta.
        AriResponse ariResponse = new() { Timestamp = DateTime.Now };
        History.Add(ariResponse);
        streamingResponse = ariResponse;
        streamedText      = "";
        Func<string, Task>? userDelta = onDelta;
        onDelta = async text => { streamedText = text; if (userDelta is not null) await userDelta(text); };

        while (true)
        {
            // Refresh the system message with the current checklist (it changes mid-turn as the
            // model calls update_todos). messages[0] is always the system message.
            messages[0] = new { role = "system", content = baseSystem + RenderTodoBlock() + thinkSuffix };

            // Compaction: bound context growth by stubbing the oldest tool outputs once the message
            // array exceeds COMPACT_RATIO of the window. Keeps the system/persistent blocks, user
            // messages, and the most recent tool results intact — only old, re-derivable output is dropped.
            CompactToolOutput(messages, toolResultSlots, agent.MaxContextTokens);

            // Recompute actual input token estimate from the real messages array — accounts for the
            // rebuilt system message, compacted slots, and all tool results accumulated this turn.
            // This keeps the live context bar in the control panel accurate throughout the turn.
            if (liveCallInfo is { } lci)
            {
                long totalChars = messages.Sum(m => (long)(ContentOf(m)?.Length ?? 0));
                lci.EstimatedInputTokens = (int)(totalChars / CHARS_PER_TOKEN);
            }

            bool      toolsExhausted = forceNoMoreTools || (agent.MaxToolCalls > 0 && toolCallCount >= agent.MaxToolCalls);
            object[]? toolSchemas    = !toolsExhausted && tools.Count > 0
                                        ? tools.Values.Select(t => t.Schema).ToArray()
                                        : null;

            if (!agent.QuietLogging && toolCallCount == 0)
                Common.Logger.LogInformation("[{Agent}] ({Thread}) {Tools}",
                    agent.Name, threadKey,
                    toolSchemas is not null ? $"{toolSchemas.Length} tool(s) available: {string.Join(", ", tools.Keys)}" : "no tools registered");

            Dictionary<string, object?> body = new()
            {
                ["model"]          = "local",
                ["messages"]       = messages,
                ["stream"]         = true,
                ["stream_options"] = new { include_usage = true },
                ["max_tokens"]     = maxTokens,
                ["temperature"]    = agent.Temperature ?? TEMPERATURE,
                ["top_p"]          = agent.TopP        ?? TOP_P,
                ["top_k"]          = TOP_K,
                ["min_p"]          = MIN_P,
                ["repeat_penalty"] = REPEAT_PENALTY
            };

            if (!agent.Think)
            {
                body["thinking"]             = false;
                body["enable_thinking"]      = false;
                body["chat_template_kwargs"] = new { enable_thinking = false };
            }
            else if (agent.ThinkingBudget > 0 || thinkingBudgetOverride > 0)
            {
                int budget = thinkingBudgetOverride > 0 ? thinkingBudgetOverride : agent.ThinkingBudget;
                body["thinking_budget"]      = budget;
                body["chat_template_kwargs"] = new { enable_thinking = true, thinking_budget = budget };
            }

            if (toolSchemas is not null) body["tools"]   = toolSchemas;
            if (agent.Slot.HasValue)     body["id_slot"] = agent.Slot.Value;

            string             json    = JsonSerializer.Serialize(body);
            HttpRequestMessage request = new(HttpMethod.Post, $"{agent.Endpoint}/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                string errBody = "";
                try { errBody = await response.Content.ReadAsStringAsync(ct); } catch { /* ignore */ }

                // llama-server rejects tool calls whose arguments contain unescaped characters
                // (e.g. writing XAML/XML with embedded double-quotes). Treat this as a recoverable
                // tool error so the model can retry with properly escaped content rather than
                // crashing the entire pipeline.
                if (response.StatusCode == System.Net.HttpStatusCode.InternalServerError
                    && errBody.Contains("Failed to parse tool call arguments", StringComparison.OrdinalIgnoreCase))
                {
                    parseFailures++;
                    Degrade();
                    if (parseFailures > 2)
                        throw new LlmRequestFailedException($"Tool call JSON parse failed {parseFailures} times in a row — aborting to prevent infinite loop.");

                    Common.Logger.LogWarning("[{Agent}] ({Thread}) Tool call JSON parse failure — injecting recovery hint.", agent.Name, threadKey);
                    string hint = "One of your tool call arguments contained characters (such as unescaped double-quotes in XML/XAML content) that made the JSON invalid. " +
                                  "Please retry: escape all double-quotes inside string values as \\\" and avoid raw newlines inside JSON strings.";
                    messages.Add(new { role = "user", content = hint });
                    continue;
                }

                throw new LlmRequestFailedException($"LLM request failed with status: {response.StatusCode}" + (errBody.Length > 0 ? $" — {errBody[..Math.Min(errBody.Length, 300)]}" : ""));
            }

            using Stream      stream = await response.Content.ReadAsStreamAsync(ct);
            using StreamReader reader = new(stream);

            Dictionary<int, (string Id, string Name, StringBuilder Args)> pendingCalls = new();
            // Tracks the most recently emitted streaming start marker per tool-call index.
            // Used to replace (not duplicate) the marker as line counts grow during streaming.
            Dictionary<int, string> streamingMarkers = new();
            string? finishReason = null;
            string? xmlFallbackOriginalText = null;
            responseBuilder.Clear();

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;

                string payload = line["data: ".Length..];
                if (payload == "[DONE]") break;

                JsonDocument chunk;
                try { chunk = JsonDocument.Parse(payload); }
                catch { continue; }

                using (chunk)
                {
                    if (chunk.RootElement.TryGetProperty("usage", out JsonElement usage))
                    {
                        completionTokens = usage.TryGetProperty("completion_tokens", out JsonElement ctEl) ? ctEl.GetInt32() : 0;
                        promptTokens     = usage.TryGetProperty("prompt_tokens",     out JsonElement ptEl) ? ptEl.GetInt32() : 0;
                    }

                    if (!chunk.RootElement.TryGetProperty("choices", out JsonElement choices) || choices.GetArrayLength() == 0) continue;

                    JsonElement choice = choices[0];

                    if (choice.TryGetProperty("finish_reason", out JsonElement frEl) && frEl.ValueKind != JsonValueKind.Null)
                        finishReason = frEl.GetString();

                    JsonElement delta = choice.GetProperty("delta");

                    if (delta.TryGetProperty("reasoning_content", out JsonElement reasoning))
                    {
                        string? thinkDelta = reasoning.GetString();
                        if (!string.IsNullOrEmpty(thinkDelta) && !wasThinking)
                        {
                            if (!agent.Think)
                                Common.Logger.LogWarning("[{Agent}] ({Thread}) thinking chain detected — <|think_off|> may not be working.", agent.Name, threadKey);
                            wasThinking = true;
                        }
                    }

                    if (delta.TryGetProperty("tool_calls", out JsonElement toolCallsEl))
                    {
                        // Native tool call detected. Move any pre-tool text (e.g. acknowledgment sentence)
                        // into contentBuilder so it survives the reset. Discard only if it looks like
                        // the model was leaking a text-format tool call before the native one.
                        if (responseBuilder.Length > 0)
                        {
                            string preText = responseBuilder.ToString().TrimEnd();
                            bool isLeakedToolCall = preText.Contains("<tool_call>") || preText.Contains("<function=")
                                || tools.Keys.Any(k => preText.StartsWith(k, StringComparison.OrdinalIgnoreCase));
                            if (!isLeakedToolCall && preText.Length > 0)
                            {
                                contentBuilder.Append(preText + "\n");
                                if (!agent.QuietLogging)
                                    Common.Logger.LogInformation("[{Agent}] ({Thread}) \"{Text}\"", agent.Name, threadKey, preText);
                            }
                            responseBuilder.Clear();
                            if (onDelta is not null) await onDelta(contentBuilder.ToString());
                        }

                        foreach (JsonElement tc in toolCallsEl.EnumerateArray())
                        {
                            int index = tc.GetProperty("index").GetInt32();

                            if (tc.TryGetProperty("id", out JsonElement idEl))
                            {
                                string id   = idEl.GetString() ?? string.Empty;
                                string name = tc.TryGetProperty("function", out JsonElement fn) && fn.TryGetProperty("name", out JsonElement nameEl)
                                    ? nameEl.GetString() ?? string.Empty
                                    : string.Empty;
                                pendingCalls[index] = (id, name, new StringBuilder());
                                consecutiveFallbacks = 0;
                            }

                            if (tc.TryGetProperty("function", out JsonElement funcEl) &&
                                funcEl.TryGetProperty("arguments", out JsonElement argsEl))
                            {
                                string? argsDelta = argsEl.GetString();
                                if (!string.IsNullOrEmpty(argsDelta) && pendingCalls.TryGetValue(index, out (string Id, string Name, StringBuilder Args) call))
                                {
                                    call.Args.Append(argsDelta);

                                    // Live streaming start marker: update counts as args stream in.
                                    if (tools.TryGetValue(call.Name, out var liveTool) && liveTool.StreamingDisplay is not null)
                                    {
                                        string? newMarker = liveTool.StreamingDisplay(call.Args.ToString());
                                        if (newMarker != null)
                                        {
                                            if (streamingMarkers.TryGetValue(index, out string? prevMarker))
                                            {
                                                if (newMarker != prevMarker)
                                                {
                                                    ReplaceInBuilder(contentBuilder, prevMarker, newMarker);
                                                    streamingMarkers[index] = newMarker;
                                                    if (onDelta is not null) await onDelta(contentBuilder.ToString());
                                                }
                                            }
                                            else
                                            {
                                                contentBuilder.Append(newMarker);
                                                streamingMarkers[index] = newMarker;
                                                if (onDelta is not null) await onDelta(contentBuilder.ToString());
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        continue;
                    }

                    if (!delta.TryGetProperty("content", out JsonElement contentEl)) continue;
                    string? deltaText = contentEl.GetString();
                    if (!string.IsNullOrEmpty(deltaText))
                    {
                        // Strip Qwen3 control tokens before accumulating
                        deltaText = deltaText
                            .Replace("<|think_off|>", "")
                            .Replace("<|think_on|>",  "")
                            .Replace("<|tool_code_start|>", "")
                            .Replace("<|tool_code_end|>",   "")
                            .Replace("<|tool_call|>",       "");
                        if (string.IsNullOrEmpty(deltaText)) continue;
                        responseBuilder.Append(deltaText);
                        if (LiveCall is { } lc) lc.EstimatedOutputTokens = responseBuilder.Length / CHARS_PER_TOKEN;
                        if (onDelta is not null)
                        {
                            // Suppress "ARI: " prefix while it's still arriving; emit stripped text once confirmed absent or past it
                            const string AriPrefix = "ARI: ";
                            string accumulated = responseBuilder.ToString();
                            string visible = accumulated.Length < AriPrefix.Length
                                ? (accumulated.StartsWith(AriPrefix[..accumulated.Length], StringComparison.OrdinalIgnoreCase) ? "" : accumulated)
                                : (accumulated.StartsWith(AriPrefix, StringComparison.OrdinalIgnoreCase) ? accumulated[AriPrefix.Length..] : accumulated);
                            await onDelta(contentBuilder.ToString() + visible);
                        }
                    }
                }
            }

            if (pendingCalls.Count == 0 && responseBuilder.Length > 0)
            {
                // Detect Qwen3 <|tool_code_start|> / <|tool_call|> fragments — these are stripped
                // from the display stream above, but if they appear it means the model tried to
                // emit a tool call in an unsupported format with no parseable arguments.
                string rawResponse = responseBuilder.ToString();
                if (rawResponse.Contains("<|tool_code_start|>") || rawResponse.Contains("<|tool_call|>"))
                {
                    consecutiveFallbacks++;
                    Degrade();
                    if (consecutiveFallbacks > 3)
                        throw new LlmRequestFailedException($"Model stuck in tool_code_start fallback loop ({consecutiveFallbacks} consecutive) — aborting.");
                    Common.Logger.LogWarning("[{Agent}] ({Thread}) model used <|tool_code_start|> format — cannot parse, injecting correction.", agent.Name, threadKey);
                    messages.Add(new { role = "assistant", content = rawResponse.Replace("<|tool_code_start|>", "").Replace("<|tool_code_end|>", "").Replace("<|tool_call|>", "").Trim() });
                    messages.Add(new { role = "user", content = "[System: Your last response contained tool call markers (<|tool_code_start|> or <|tool_call|>) with no parseable arguments. Do not use these markers. Issue tool calls using only the proper JSON function-call format.]" });
                    responseBuilder.Clear();
                    continue;
                }

                List<ToolCallParser.Call>? textCalls = ToolCallParser.ParseTextCalls(responseBuilder.ToString());
                if (textCalls is not null)
                {
                    consecutiveFallbacks++;
                    Degrade();
                    if (consecutiveFallbacks > 3)
                        throw new LlmRequestFailedException($"Model stuck in text tool call fallback loop ({consecutiveFallbacks} consecutive) — aborting.");
                    Common.Logger.LogWarning("[{Agent}] ({Thread}) model used text tool call format — parsing fallback.", agent.Name, threadKey);
                    int fakeIndex = 0;
                    foreach (ToolCallParser.Call c in textCalls)
                        pendingCalls[fakeIndex++] = (c.Id, c.Name, new StringBuilder(c.Args));

                    responseBuilder.Clear();
                    finishReason = "tool_calls";
                }

                // Qwen3 XML tool call format: <tool_name><param>value</param>...</tool_name>
                if (pendingCalls.Count == 0 && tools.Count > 0)
                {
                    ToolCallParser.XmlParse? xml = ToolCallParser.ParseXmlCalls(responseBuilder.ToString(), tools.Keys);
                    if (xml is not null)
                    {
                        consecutiveFallbacks++;
                        Degrade();
                        if (consecutiveFallbacks > 3)
                            throw new LlmRequestFailedException($"Model stuck in XML tool call fallback loop ({consecutiveFallbacks} consecutive) — aborting.");
                        Common.Logger.LogWarning("[{Agent}] ({Thread}) model used Qwen3 XML tool call format — parsing fallback.", agent.Name, threadKey);

                        // Preserve the full original response (with XML) as the assistant turn
                        xmlFallbackOriginalText = responseBuilder.ToString();

                        // Extract any text before the first tool call for display
                        if (xml.FirstIndex > 0)
                            contentBuilder.Append(xmlFallbackOriginalText[..xml.FirstIndex].TrimEnd());

                        int fakeIndex = 0;
                        foreach (ToolCallParser.Call c in xml.Calls)
                            pendingCalls[fakeIndex++] = (c.Id, c.Name, new StringBuilder(c.Args));

                        responseBuilder.Clear();
                        finishReason = "tool_calls";
                    }
                }
            }

            if (pendingCalls.Count > 0 && (finishReason == "tool_calls" || finishReason == "stop" || finishReason == null))
            {
                // Strip any <think>...</think> leakage then repair unescaped quotes in tool call args.
                // Both must happen before args are used for display, execution, or sent back to llama-server.
                foreach (var key in pendingCalls.Keys)
                {
                    var (id, name, args) = pendingCalls[key];
                    string raw      = args.ToString();
                    string stripped = ToolCallParser.StripThinkLeaks(raw);
                    string repaired = ToolCallParser.RepairArgs(stripped);

                    if (stripped != raw)
                        Common.Logger.LogWarning("[{Agent}] ({Thread}) Stripped <think> leakage from args for tool '{Tool}'.", agent.Name, threadKey, name);
                    if (repaired != stripped)
                        Common.Logger.LogWarning("[{Agent}] ({Thread}) Repaired malformed JSON args for tool '{Tool}'.", agent.Name, threadKey, name);

                    if (repaired != raw)
                        pendingCalls[key] = (id, name, new StringBuilder(repaired));
                }

                bool isXmlFallback = pendingCalls.Values.Any(c => c.Id.StartsWith("fallback_xml_"));

                toolCallCount += pendingCalls.Count;

                if (isXmlFallback)
                {
                    // XML fallback: model generated XML in content — it won't recognise role:tool.
                    // Keep the original XML as the assistant turn, inject results as role:user.
                    messages.Add(new { role = "assistant", content = xmlFallbackOriginalText ?? "" });
                }
                else
                {
                    // Native tool_calls: use standard OpenAI format — the model and llama-server
                    // both understand role:tool for this path (same as Dialogue/Recall).
                    // Strip large content fields from write_file/edit_file args to avoid context bloat.
                    var toolCallList = pendingCalls
                        .OrderBy(kv => kv.Key)
                        .Select(kv => new
                        {
                            id       = kv.Value.Id,
                            type     = "function",
                            function = new { name = kv.Value.Name, arguments = ToolCallParser.TrimArgs(kv.Value.Name, kv.Value.Args.ToString()) }
                        })
                        .ToArray();

                    messages.Add(new { role = "assistant", tool_calls = toolCallList });
                }

                // Execute each tool, emit display markers, collect results
                StringBuilder? xmlResultsMsg = isXmlFallback
                    ? new StringBuilder("Here are the results of the tool calls you made:\n\n")
                    : null;

                editedPathsThisBatch.Clear();

                // Parallelism: kick off independent read-only tool executions concurrently. The loop
                // below still processes every call in order — only the I/O overlaps — so all guards,
                // eviction and message ordering stay exactly sequential. Mutating tools run inline.
                HashSet<string> readOnlyTools = new(StringComparer.OrdinalIgnoreCase)
                    { "read_file", "search_files", "list_directory", "find_files" };
                Dictionary<int, Task<string>> prelaunched = new();
                if (pendingCalls.Count > 1)
                    foreach (var (idx, c) in pendingCalls)
                        if (readOnlyTools.Contains(c.Name) && tools.TryGetValue(c.Name, out var roTool))
                            prelaunched[idx] = roTool.Execute(c.Args.ToString());

                foreach (var (callIndex, call) in pendingCalls)
                {
                    string result;

                    // Soft re-read guard: if the model reads the same file more than 3 times
                    // in one turn it's looping. Return a nudge instead of the file content again.
                    if (call.Name is "read_file")
                    {
                        try
                        {
                            using JsonDocument rdoc = JsonDocument.Parse(call.Args.ToString());
                            string rpath = NormKey(rdoc.RootElement.GetProperty("path").GetString() ?? "");
                            readCounts.TryGetValue(rpath, out int rc);
                            readCounts[rpath] = rc + 1;
                            if (rc >= 1)
                            {
                                result = $"[System: You have already read {rpath} this turn. Do not read it again — use the content you already have. If you need a specific section, use search_files to find the line numbers, then read_file with start_line/end_line.]";
                                if (isXmlFallback)
                                {
                                    xmlResultsMsg!.AppendLine($"--- {call.Name} ---");
                                    xmlResultsMsg.AppendLine(result);
                                    xmlResultsMsg.AppendLine();
                                }
                                else
                                {
                                    messages.Add(new { role = "tool", tool_call_id = call.Id, name = call.Name, content = result });
                                    if (liveCallInfo is { } lc) lc.EstimatedInputTokens += result.Length / CHARS_PER_TOKEN;
                                }
                                continue;
                            }
                        }
                        catch { /* ignore — proceed normally */ }
                    }

                    // One-edit-per-file-per-batch guard: a second edit to the same file in one batch
                    // was written against the file's pre-edit-#1 content, so reject it and make the
                    // model re-read before editing again (next iteration). Different files still run.
                    if (call.Name == "edit_file")
                    {
                        string? editPath = null;
                        try
                        {
                            using JsonDocument edoc = JsonDocument.Parse(call.Args.ToString());
                            editPath = (edoc.RootElement.GetProperty("path").GetString() ?? "").Trim('"', '\'', ' ', '\\');
                        }
                        catch { /* unparseable — fall through to normal handling/error */ }

                        if (!string.IsNullOrEmpty(editPath))
                        {
                            string key = NormKey(editPath);

                            if (editedPathsThisBatch.Contains(key))
                            {
                                result = $"[System: edit_file was already applied to {editPath} earlier in this same batch of tool calls. This second edit was NOT applied — its old_string was written against the file's previous content and would fail or corrupt it. Re-read {editPath}, then make the next edit.]";
                                string skipLabel = System.IO.Path.GetFileName(editPath);
                                contentBuilder.Append($"<!--ari-tool-error:edit_file:{skipLabel}:{ToolCallParser.EscapeLabel(result)}-->");
                                if (onDelta is not null) await onDelta(contentBuilder.ToString());
                                if (isXmlFallback)
                                {
                                    xmlResultsMsg!.AppendLine($"--- {call.Name} ---");
                                    xmlResultsMsg.AppendLine(result);
                                    xmlResultsMsg.AppendLine();
                                }
                                else
                                {
                                    messages.Add(new { role = "tool", tool_call_id = call.Id, name = call.Name, content = result });
                                    if (liveCallInfo is { } lc) lc.EstimatedInputTokens += result.Length / CHARS_PER_TOKEN;
                                }
                                continue;
                            }
                            editedPathsThisBatch.Add(key);
                        }
                    }

                    // One-time checklist nudge: when the model begins changing a SECOND distinct file
                    // with no checklist, require a plan first. This is the mechanical backstop for a
                    // model that ignores the prose rule to call update_todos on multi-file work.
                    if (!todoNudged && todos.Count == 0 && call.Name is "edit_file" or "write_file")
                    {
                        string? tp = null;
                        try
                        {
                            using JsonDocument tdoc = JsonDocument.Parse(call.Args.ToString());
                            tp = NormKey(tdoc.RootElement.GetProperty("path").GetString() ?? "");
                        }
                        catch { /* unparseable — skip the nudge, let normal handling report the error */ }

                        if (!string.IsNullOrEmpty(tp) && turnEditPaths.Count >= 1 && !turnEditPaths.Contains(tp))
                        {
                            todoNudged = true;
                            result = $"[System: You are now changing a second file ({tp}) but have no task checklist. Before this edit, call update_todos with the full plan — one item per file/change, and include updating call sites, tests, and building as their own items. Then make this edit. Maintaining the checklist is required for multi-file work.]";
                            string nudgeLabel = System.IO.Path.GetFileName(tp);
                            contentBuilder.Append($"<!--ari-tool-error:{call.Name}:{nudgeLabel}:{ToolCallParser.EscapeLabel(result)}-->");
                            if (onDelta is not null) await onDelta(contentBuilder.ToString());
                            if (isXmlFallback)
                            {
                                xmlResultsMsg!.AppendLine($"--- {call.Name} ---");
                                xmlResultsMsg.AppendLine(result);
                                xmlResultsMsg.AppendLine();
                            }
                            else
                            {
                                messages.Add(new { role = "tool", tool_call_id = call.Id, name = call.Name, content = result });
                                if (liveCallInfo is { } lc) lc.EstimatedInputTokens += result.Length / CHARS_PER_TOKEN;
                            }
                            continue;
                        }
                        if (!string.IsNullOrEmpty(tp)) turnEditPaths.Add(tp);
                    }
                    else if (call.Name is "edit_file" or "write_file")
                    {
                        // Checklist exists (or already nudged): still record the file so the nudge's
                        // "second distinct file" trigger stays accurate if todos are later cleared.
                        try
                        {
                            using JsonDocument tdoc = JsonDocument.Parse(call.Args.ToString());
                            string tp = NormKey(tdoc.RootElement.GetProperty("path").GetString() ?? "");
                            if (!string.IsNullOrEmpty(tp)) turnEditPaths.Add(tp);
                        }
                        catch { /* ignore */ }
                    }

                    if (tools.TryGetValue(call.Name, out var tool))
                    {
                        if (tool.Display is not null)
                        {
                            string finalMarker = tool.Display(call.Args.ToString());
                            if (streamingMarkers.TryGetValue(callIndex, out string? prevStreamMarker))
                                // Streaming already emitted a start marker — replace it with final counts.
                                ReplaceInBuilder(contentBuilder, prevStreamMarker, finalMarker);
                            else
                                contentBuilder.Append(finalMarker);
                            if (onDelta is not null) await onDelta(contentBuilder.ToString());
                        }
                        // Use the pre-launched (concurrent) result for read-only tools; otherwise run now.
                        result = prelaunched.TryGetValue(callIndex, out Task<string>? pre)
                            ? await pre
                            : await tool.Execute(call.Args.ToString());

                        // Track edits and enrich stale old_string errors with a re-read hint.
                        if (call.Name == "edit_file")
                        {
                            try
                            {
                                using JsonDocument argDoc = JsonDocument.Parse(call.Args.ToString());
                                string editPath = (argDoc.RootElement.GetProperty("path").GetString() ?? "").Trim('"', '\'', ' ');
                                string editKey  = NormKey(editPath);
                                // Contains (not StartsWith): the web-panel path wraps errors as
                                // "[Error: old_string not found ...]", so StartsWith would miss them.
                                // Any non-success outcome counts as a failure — not just
                                // old_string-not-found, but also permission/IO errors. (A transient
                                // permission error forcing the model onto sed/grep is exactly what
                                // collapsed an earlier run, so we steer it back to read+write here.)
                                bool edited = result.Contains("Successfully edited");
                                if (edited)
                                {
                                    editedFiles.Add(editKey);
                                    editFailStreak.Remove(editKey);
                                }
                                else
                                {
                                    editFailStreak.TryGetValue(editKey, out int streak);
                                    editFailStreak[editKey] = ++streak;

                                    if (editedFiles.Contains(editKey))
                                        result += " This file was already edited earlier this turn — re-read it to see the current content before retrying.";
                                    // Don't retype text that didn't match. Point the model at the path it
                                    // already has the information for: line-anchored editing (it can see the
                                    // line numbers) or a full rewrite. No hard cutoff — if it still can't,
                                    // the prompt tells it to stop and ask the user.
                                    if (streak >= 2)
                                        result += " Stop retyping the text. You have the line numbers from read_file/search_files — change these lines with start_line/end_line instead of old_string (one edit_file call with an 'edits' array if several lines), or rewrite the whole file with write_file. If you still cannot, stop and tell the user what is blocking you.";
                                }
                            }
                            catch { /* ignore */ }
                        }

                        // Guard against the rewrite spiral: a model that distrusts its own successful
                        // write_file regenerates the whole file over and over (minutes each). Nudge on
                        // the second write, cut off tools on the third.
                        if (call.Name == "write_file" && result.Contains("Successfully wrote"))
                        {
                            try
                            {
                                using JsonDocument argDoc = JsonDocument.Parse(call.Args.ToString());
                                string writePath = NormKey(argDoc.RootElement.GetProperty("path").GetString() ?? "");
                                // A full rewrite supersedes the file's edit history: clear the
                                // edit-fail streak so the spiral-breaker doesn't keep blocking it.
                                editFailStreak.Remove(writePath);
                                editedFiles.Add(writePath);
                                writeCounts.TryGetValue(writePath, out int wc);
                                writeCounts[writePath] = ++wc;
                                if (wc == 2)
                                    result += " You have already written this file this turn and that write succeeded. Do NOT write it again unless you have a further, distinct change. If you are unsure the content is correct, use read_file to verify — do not rewrite it blindly.";
                                else if (wc >= 3)
                                {
                                    forceNoMoreTools = true;
                                    result += " This file has been written too many times this turn. No further tool calls will be accepted — tell the user the file has been updated and stop.";
                                    Common.Logger.LogWarning("[{Agent}] ({Thread}) write_file called {Count}x on '{File}' — cutting off tools for this turn.", agent.Name, threadKey, wc, writePath);
                                }
                            }
                            catch { /* ignore */ }
                        }

                        if (ToolCallParser.IsError(result))
                        {
                            Common.Logger.LogError("[{Agent}] ({Thread}) Tool '{Tool}' failed: {Error}", agent.Name, threadKey, call.Name, result);
                            string errLabel = "";
                            try
                            {
                                using JsonDocument argDoc = JsonDocument.Parse(call.Args.ToString());
                                string p = argDoc.RootElement.TryGetProperty("path",    out var pe)  ? pe.GetString()  ?? "" :
                                           argDoc.RootElement.TryGetProperty("pattern", out var pte) ? pte.GetString() ?? "" : "";
                                errLabel = System.IO.Path.GetFileName(p.Trim('"', '\'', ' ', '\\'));
                            }
                            catch { /* ignore */ }
                            contentBuilder.Append($"<!--ari-tool-error:{call.Name}:{errLabel}:{ToolCallParser.EscapeLabel(result)}-->");
                            if (onDelta is not null) await onDelta(contentBuilder.ToString());
                        }
                        else if (tool.DisplayAfter is not null)
                        {
                            contentBuilder.Append(tool.DisplayAfter(call.Args.ToString()));
                            if (onDelta is not null) await onDelta(contentBuilder.ToString());
                        }
                        toolResults.Add(result);
                    }
                    else
                    {
                        result = $"[Error: tool '{call.Name}' is not registered]";
                        Common.Logger.LogError("[{Agent}] ({Thread}) Model called unknown tool '{Tool}'", agent.Name, threadKey, call.Name);
                        contentBuilder.Append($"<!--ari-tool-error:{call.Name}:{ToolCallParser.EscapeLabel(result)}-->");
                        if (onDelta is not null) await onDelta(contentBuilder.ToString());
                    }

                    if (isXmlFallback)
                    {
                        xmlResultsMsg!.AppendLine($"--- {call.Name} ---");
                        xmlResultsMsg.AppendLine(result);
                        xmlResultsMsg.AppendLine();
                    }
                    else
                    {
                        int addedIndex = messages.Count;
                        messages.Add(new { role = "tool", tool_call_id = call.Id, name = call.Name, content = result });
                        toolResultSlots.Add((addedIndex, call.Id, call.Name));
                        if (liveCallInfo is { } lc) lc.EstimatedInputTokens += result.Length / CHARS_PER_TOKEN;

                        // Keep only the newest snapshot of any file in context.
                        //  - a fresh read_file supersedes the previous read of that path
                        //  - a successful edit_file / write_file makes the prior read stale
                        // In both cases the earlier full-content message is replaced with a short stub.
                        try
                        {
                            using JsonDocument hdoc = JsonDocument.Parse(call.Args.ToString());
                            string hpath = hdoc.RootElement.TryGetProperty("path", out var hpe)
                                ? (hpe.GetString() ?? "").Trim('"', '\'', ' ', '\\') : "";
                            if (!string.IsNullOrEmpty(hpath))
                            {
                                if (call.Name == "read_file")
                                {
                                    if (liveReads.TryGetValue(hpath, out var prev))
                                        StubRead(messages, prev.Index, prev.CallId, hpath);
                                    // Only the actual content read counts as the live copy, not the
                                    // re-read guard's nudge (which never reaches this branch).
                                    if (!result.StartsWith("[System:"))
                                        liveReads[hpath] = (addedIndex, call.Id);
                                }
                                else if (call.Name is "edit_file" or "write_file"
                                         && (result.Contains("Successfully edited") || result.Contains("Successfully wrote")))
                                {
                                    if (liveReads.TryGetValue(hpath, out var prev))
                                    {
                                        StubRead(messages, prev.Index, prev.CallId, hpath);
                                        liveReads.Remove(hpath);
                                    }
                                }
                            }
                        }
                        catch { /* ignore — leave context as-is */ }
                    }
                }

                if (isXmlFallback)
                {
                    string xmlMsg = xmlResultsMsg!.ToString().TrimEnd();
                    messages.Add(new { role = "user", content = xmlMsg });
                    if (liveCallInfo is { } lc) lc.EstimatedInputTokens += xmlMsg.Length / CHARS_PER_TOKEN;
                }

                // Mark end of this tool batch so the UI can keep cards as "Reading" within a
                // batch but flip them to "Read" once a new batch or text follows.
                contentBuilder.Append("<!--ari-batch-end-->");
                if (onDelta is not null) await onDelta(contentBuilder.ToString());

                // If this iteration used a fallback format, inject a correction hint.
                bool wasFallback = pendingCalls.Values.Any(c => c.Id.StartsWith("fallback_"));
                if (wasFallback)
                {
                    string correctionHint =
                        $"[System: Your previous tool calls ({string.Join(", ", pendingCalls.Values.Select(c => c.Name))}) were emitted as plain text instead of " +
                        "the required JSON function-call format. The results have been provided above. " +
                        "Please continue using only the proper JSON function-call format for any further tool calls.]";
                    messages.Add(new { role = "user", content = correctionHint });
                }

                continue;
            }

            // Finish-time checklist guard: if the model is about to stop while checklist items are
            // still incomplete, remind it ONCE before letting it finish — catches a genuinely dropped
            // sub-task. Capped at one so it can never trap the model when it is deliberately stopping
            // to report a blocker back to the user (escalating is a valid outcome).
            bool toolsStillAvailable = !forceNoMoreTools && !(agent.MaxToolCalls > 0 && toolCallCount >= agent.MaxToolCalls);
            if (pendingCalls.Count == 0 && IncompleteTodoCount() > 0 && todoReminders < 1 && toolsStillAvailable)
            {
                todoReminders++;
                string pending = string.Join("\n", todos.Where(t => t.Status != "completed").Select(t => $"- {t.Content} ({t.Status})"));
                Common.Logger.LogInformation("[{Agent}] ({Thread}) finish-time checklist reminder ({Count} incomplete).", agent.Name, threadKey, IncompleteTodoCount());
                messages.Add(new { role = "user", content =
                    $"[System: You still have incomplete checklist items:\n{pending}\n" +
                    "Complete them now (make the changes, then call update_todos to mark them completed), " +
                    "or call update_todos to remove any that are no longer needed. Do not finish until the checklist is resolved.]" });
                responseBuilder.Clear();
                continue;
            }

            break;
        }

        sw.Stop();
        string responseText = contentBuilder.Length > 0
            ? contentBuilder.ToString() + responseBuilder.ToString()
            : responseBuilder.ToString();
        if (responseText.StartsWith("ARI: ", StringComparison.OrdinalIgnoreCase))
            responseText = responseText["ARI: ".Length..];
        // Strip Qwen3 thinking control tokens that leak through llama-server
        responseText = responseText
            .Replace("<|think_off|>", "")
            .Replace("<|think_on|>", "")
            .Trim();
        if (string.IsNullOrWhiteSpace(responseText))
            throw new LlmRequestFailedException("LLM response was empty.");

        double elapsed   = sw.Elapsed.TotalSeconds;
        double tokPerSec = completionTokens > 0 ? completionTokens / elapsed : 0;

        if (!agent.QuietLogging)
        {
            Common.Logger.LogInformation("[{Agent}] ({Thread}) responded in {Seconds}s ({Tokens} tokens, {TokPerSec} t/s)",
                agent.Name, threadKey, elapsed.ToString("F1"), completionTokens, tokPerSec.ToString("F1"));

            if (maxTokens > 0 && completionTokens >= maxTokens * TOKEN_WARNING_RATIO)
                Common.Logger.LogWarning("[{Agent}] ({Thread}) token usage at {Pct}% of limit ({Used}/{Max})",
                    agent.Name, threadKey, (int)(completionTokens * 100.0 / maxTokens), completionTokens, maxTokens);

            Common.Logger.LogInformation("[{Agent}] ({Thread}) response\n\"{Response}\"",
                agent.Name, threadKey, ExtractLogText(responseText));
        }

        List<string> noteParts = new();
        if (!string.IsNullOrEmpty(recallNotes)) noteParts.Add(recallNotes.Trim());
        if (toolResults.Count > 0)              noteParts.Add(string.Join("\n\n", toolResults).TrimEnd());
        string? combinedNotes = noteParts.Count > 0 ? string.Join("\n\n", noteParts) : null;

        liveCallInfo = null;

        ariResponse.Content                   = AriContentBlock.Parse(responseText);
        ariResponse.ThinkingSeconds           = elapsed;
        ariResponse.RecallNotes               = combinedNotes;
        ariResponse.ContextSummary            = contextSummary;
        ariResponse.CompletionTokens          = completionTokens;
        ariResponse.OutputTokenLimit          = maxTokens > 0 ? maxTokens : 0;
        ariResponse.PromptTokens              = promptTokens;
        ariResponse.ContextTokenLimit         = agent.MaxContextTokens;
        ariResponse.HadImageAttachments       = hadImages;
        ariResponse.EstimatedTextPromptTokens = estimatedTextTokens;
        ariResponse.ImageTokenLimit           = agent.MaxImageTokens;
        ariResponse.State                     = AriResponseState.Complete;
        streamingResponse                     = null;
        Updated?.Invoke();

        ariRepliedAt = DateTime.UtcNow;
        inactivityTimer?.Dispose();
        inactivityTimer = new Timer(_ =>
        {
            if (State != ThreadState.Active) return;
            State = ThreadState.Inactive;
            BecameInactive?.Invoke();
        }, null, InactivityThreshold, Timeout.InfiniteTimeSpan);

        ExchangeCompleted?.Invoke(prompt, responseText);

        if (agent.MemoryLimit > 0 && History.Count >= agent.MemoryLimit)
        {
            int engramInterval = Math.Max(1, agent.MemoryLimit / 2);
            if (History.Count == agent.MemoryLimit || History.Count % engramInterval == 0)
                BufferFull?.Invoke();
        }

        return responseText;
    }

    /// <summary>Strips tool-use markers from a response string, returning only the prose text.</summary>
    private static string ExtractLogText(string content) =>
        string.Concat(AriContentBlock.Parse(content).OfType<TextBlock>().Select(b => b.Text))
            .Replace("<!--ari-batch-end-->", "")
            .Trim();

    /// <summary>
    /// Replaces a stale read_file tool-result in the messages array with a short stub, preserving
    /// the tool_call_id/role/name so the assistant↔tool pairing stays valid for llama-server.
    /// </summary>
    private static void StubRead(List<object> messages, int index, string callId, string path)
    {
        if (index < 0 || index >= messages.Count) return;
        messages[index] = new
        {
            role         = "tool",
            tool_call_id = callId,
            name         = "read_file",
            content      = $"[Earlier contents of {path} omitted — superseded by a later read or change this turn. Re-read the file if you need its current contents.]"
        };
    }

    /// <summary>The string content of a message object (system/user/tool), or null for tool_calls turns.</summary>
    private static string? ContentOf(object m) => m.GetType().GetProperty("content")?.GetValue(m) as string;

    /// <summary>
    /// Context compaction: once the message array exceeds COMPACT_RATIO of the context window, replace
    /// the oldest tool-result outputs (keeping the most recent COMPACT_KEEP_RECENT) with short stubs.
    /// Bounds context growth on long turns — the main defence against the model's tool-call formatting
    /// degrading. Preserves role/tool_call_id/name so the assistant↔tool pairing stays valid.
    /// </summary>
    private static void CompactToolOutput(List<object> messages, List<(int Index, string CallId, string Name)> slots, int maxContextTokens)
    {
        if (maxContextTokens <= 0) return;

        // Always compact: stub tool results older than COMPACT_KEEP_RECENT regardless of total size.
        // The model has already processed earlier results; keeping them verbatim wastes context.
        // Only fall back to the budget check to stub even more aggressively when context is filling up.
        long budget = (long)(maxContextTokens * (long)CHARS_PER_TOKEN * COMPACT_RATIO);
        long total  = 0;
        foreach (object m in messages) total += ContentOf(m)?.Length ?? 0;

        // Stub all results beyond COMPACT_KEEP_RECENT (always), then continue stubbing older
        // ones until we're under budget if context is still filling up.
        int stubbable = slots.Count - COMPACT_KEEP_RECENT;
        for (int i = 0; i < stubbable; i++)
        {
            (int idx, string callId, string name) = slots[i];
            if (idx < 0 || idx >= messages.Count) continue;
            string? cur = ContentOf(messages[idx]);
            if (cur is null || cur.Length < 200) continue;   // already small / already stubbed — skip
            string stub = $"[Earlier {name} output omitted to save context — re-run the tool if you need it again.]";
            messages[idx] = new { role = "tool", tool_call_id = callId, name, content = stub };
            total -= cur.Length - stub.Length;
        }
    }

    /// <summary>Finds the last occurrence of <paramref name="oldText"/> in <paramref name="sb"/> and replaces it with <paramref name="newText"/>.</summary>
    private static void ReplaceInBuilder(StringBuilder sb, string oldText, string newText)
    {
        string s = sb.ToString();
        int pos = s.LastIndexOf(oldText, StringComparison.Ordinal);
        if (pos < 0) return;
        sb.Remove(pos, oldText.Length).Insert(pos, newText);
    }
}

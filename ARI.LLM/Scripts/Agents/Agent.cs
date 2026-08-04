using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public abstract class Agent
{
    // ── JSON-serialised fields (common to all agents) ────────────────────────
    public string Name { get; init; } = "";
    public string ServerName { get; set; } = "";
    public string SystemPrompt { get; init; } = "";
    public Dictionary<string, string>? PromptTemplates { get; init; }
    
    // Tool groups registered eagerly (skips the request_tools round-trip). Null/empty = defer all.
    public string[]? PreloadedTools { get; init; }
    public bool Enabled { get; init; }
    public int BudgetResponse { get; init; } = -1;
    public int MaxToolCalls { get; init; }
    public bool Think { get; init; }
    public int BudgetThinking { get; init; }
    // Resolved to Slot (and id_slot) at bind time. Null = no slot pin.
    public string? SlotName { get; set; }
    public SamplerSettings? SamplerSettings { get; init; }
    [JsonIgnore] internal int BudgetContext => Slot?.ContextLimit ?? 0;

    // Compaction: off by default. When on, stubs oldest tool outputs one at a time, once usage exceeds
    // CompactHighPct of the bound slot's context, until it drops under CompactLowPct — both percentages
    // (0-100) of BudgetContext. An agent that doesn't support compaction (one-shot, no long tool-call
    // history) relies on the server's --context-shift as its only safety net instead.
    public bool SupportsCompaction { get; init; }
    public int CompactHighPct { get; init; } = 80;
    public int CompactLowPct { get; init; } = 60;
    public bool NativeTools { get; init; } = true;

    // Prepends Ari's persona as a stable prefix; set on user-facing agents, false on autonomic agents.
    public bool UsePersona { get; init; }

    // ── Runtime-only ─────────────────────────────────────────────────────────
    [JsonIgnore] public string Endpoint { get; internal set; } = "";
    [JsonIgnore] internal Server?    Server { get; set; }
    [JsonIgnore] internal NamedSlot? Slot   { get; set; }

    /// <summary>Called when the processing phase changes for a thread. Set by LLMModule to update the watch-stream status.</summary>
    [JsonIgnore] internal Action<string, ThreadPhase>? OnPhaseChange { get; set; }

    [JsonIgnore] internal virtual int  MemoryLimit => 0;  // 0 = unlimited
    internal virtual bool SuppressLog()    => false;
    [JsonIgnore] internal virtual bool LogReasoning    => false;

    // ── Constants ────────────────────────────────────────────────────────────
    private const int    CHARS_PER_TOKEN     = 4;   // measured rate for real code
    private const double TOKEN_WARNING_RATIO = 0.8;
    private const int    COMPACT_KEEP_RECENT = 3;   // never stub the N most recent tool outputs
    private const int    MAX_DEGRADE_EVENTS  = 5;
    private const int    AVERAGE_RESPONSE_WINDOW = 25;
    private const string ATTACHMENT_DIVIDER  = "-------------------";

    private readonly HttpClient httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    // ── Context-building hooks ───────────────────────────────────────────────
    internal virtual string PersistentContext(Thread thread)    => "";
    internal virtual string DynamicContext(Thread thread, bool lastStepToolOnly) => "";
    internal virtual string BuildSystemPrompt(Thread thread) => SystemPrompt;

    private static string BuildBudgets(int thinking, int reply, int context, int toolCalls)
    {
        var lines = new List<string>();

        if (thinking  > 0) lines.Add($"Thinking Token Budget: {thinking}");
        if (reply     > 0) lines.Add($"Reply Token Budget: {reply}");
        if (context   > 0) lines.Add($"Context Token Budget: {context}");
        if (toolCalls > 0) lines.Add($"Tool Call Budget: {toolCalls}");

        if (lines.Count == 0) return "";

        lines.Add("Deliver a COMPLETE answer within these budgets.");
        if (thinking > 0)
            lines.Add("Your thinking budget is tight — decide fast. Do not restate your instructions or plan at length; jump straight to the key insight and start writing.");
        return "\n\n# Budgets\n" + string.Join("\n", lines);
    }

    internal string ResolveTemplate(string key, string fallback, params (string Token, string Value)[] tokens)
    {
        string text = PromptTemplates is not null && PromptTemplates.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : fallback;
        foreach ((string token, string value) in tokens)
            text = text.Replace("{" + token + "}", value);
        return text;
    }

    internal virtual SamplerSettings ResolveSampler(Thread thread) => new();


    // ── Tool-loop hooks ──────────────────────────────────────────────────────
    // Created fresh per Prompt turn — one agent instance can serve many threads concurrently.
    internal class ToolTurnState { }
    internal virtual ToolTurnState NewTurnState() => new();
    internal virtual void OnBatchStart(ToolTurnState state) { }
    // Per-delta veto fired while tool args are streaming in; return an abort message to cancel, null to continue.
    internal virtual string? OnToolStreaming(Thread thread, ToolTurnState state, string name, string partialArgs) => null;
    internal Func<Thread, ToolTurnState, string, string, string?>? OnToolStreamingPipeline { get; set; }
    internal virtual void OnToolAdded(ToolTurnState state, List<object> messages, string toolName, string callId, string argsJson, string result, int addedIndex) { }
    internal virtual bool ShouldBreak(Thread thread, ToolTurnState state, bool productiveBatch) => false;
    // Agent-level cancellation of the tool loop (e.g. after a commit). Checked at loop top and nudge gate.
    internal virtual bool ToolsCancelled(ToolTurnState state) => false;

    public (int Used, int Limit) GetContextStats(Thread? thread)
    {
        if (thread is null) return (0, BudgetContext);
        int maxChars = BudgetContext > 0 ? (int)(BudgetContext * 3.5) : 0;
        List<ThreadMessage> ctx = thread.GetChatHistory(MemoryLimit, maxChars);
        int chars = 0;
        foreach (ThreadMessage m in ctx)
            chars += (m.Username?.Length ?? 0) + 2 + (m.Content?.Length ?? 0);
        return (chars / CHARS_PER_TOKEN, BudgetContext);
    }

    // ── Lifecycle events ─────────────────────────────────────────────────────
    // Each event: agent-layer virtual fires first (subclasses override), pipeline delegate sees the result.
    // null return = no-op / pass through; non-null = intercept/transform/redirect.

    internal virtual string OnPrompt(Thread thread, string prompt, PromptOptions opts) => prompt;
    internal Func<Thread, string, PromptOptions, string>? OnPromptPipeline { get; set; }

    // Non-null = redirect back into thinking (TalkingAgent uses this for speech steering).
    internal virtual string? OnStreamingDelta(Thread thread, string delta) => null;
    internal Func<Thread, string, string?>? OnStreamingDeltaPipeline { get; set; }

    // Non-null = interrupt reasoning and inject a redirect message.
    internal virtual string? OnThinkingDelta(Thread thread, string delta) => null;
    internal Func<Thread, string, string?>? OnThinkingDeltaPipeline { get; set; }

    // Non-null = short-circuit the tool without executing it.
    internal virtual string? OnToolCall(Thread thread, ToolTurnState state, string name, string callId, string argsJson) => null;
    internal Func<Thread, string, string, string?>? OnToolCallPipeline { get; set; }

    internal virtual string OnToolResult(Thread thread, ToolTurnState state, string name, string argsJson, string result) => result;
    internal Func<Thread, string, string, string>? OnToolResultPipeline { get; set; }

    // Non-null = inject as user message and restart the loop.
    internal virtual string? OnStepComplete(Thread thread, string stepText, bool hadTools) => null;
    internal Func<Thread, string, bool, string?>? OnStepCompletePipeline { get; set; }

    // null = suppress nudge; non-null = use as nudge text.
    internal virtual string? OnNudge(Thread thread, string nudge) => nudge;
    internal Func<Thread, string, string?>? OnNudgePipeline { get; set; }

    internal virtual string OnResponse(Thread thread, string response) => response;
    internal Func<Thread, string, string>? OnResponsePipeline { get; set; }

    // ── Send loop ────────────────────────────────────────────────────────────

    internal virtual async Task Steer(Thread thread, string steering) { }
    internal virtual void Cancel(Thread thread) { }

    internal async Task<string> Prompt(Thread thread, string prompt, PromptOptions opts)
    {
        await thread.sendLock.WaitAsync(opts.Ct);
        try
        {
            return await Send(thread, prompt, opts);
        }
        catch (OperationCanceledException)
        {
            thread.liveCallInfo = null;
            if (!thread.preserveOnCancel)
            {
                if (thread.streamingResponse is not null) thread.History.Remove(thread.streamingResponse);
                if (thread.History.Count > 0 && thread.History[^1] is Prompt) thread.History.RemoveAt(thread.History.Count - 1);
            }
            else if (thread.streamingResponse is not null)
            {
                thread.streamingResponse.Content = ContentBlock.Parse(thread.streamedText);
                thread.streamingResponse.State   = State.Cancelled;
            }
            thread.preserveOnCancel  = false;
            thread.streamingResponse = null;
            throw;
        }
        catch (Exception ex)
        {
            if (thread.streamingResponse is not null)
            {
                thread.streamingResponse.Content = ContentBlock.Parse(
                    string.IsNullOrWhiteSpace(thread.streamedText)
                        ? $"[Error: {ex.Message}]"
                        : thread.streamedText);
                thread.streamingResponse.State = State.Error;
                thread.RaiseUpdated();
            }
            thread.streamingResponse = null;
            throw;
        }
        finally
        {
            thread.liveCallInfo = null;
            thread.sendLock.Release();
            thread.OnGenerationAborted();
        }
    }

    /// <summary>Wall-clock split of one turn (#35 v2). Prefill = request sent → first delta of each request
    /// (the server reading the prompt). Thinking/Typing tick ONLY while deltas are actually arriving — an
    /// inter-delta gap over 2s is a server stall or tool wait and counts toward no bucket. The thinking
    /// budget compares against Thinking, never total elapsed, so a slow prefill can no longer eat the
    /// model's thinking allowance (a 223s prefill once consumed a 30s budget before the model said a word).</summary>
    private sealed class TurnClock
    {
        public double Prefill, Thinking, Typing;
        public ThreadPhase CurrentPhase { get; private set; } = ThreadPhase.Prefilling;
        private DateTime  sent;
        private DateTime? lastDelta;

        public void RequestSent() { sent = DateTime.UtcNow; lastDelta = null; CurrentPhase = ThreadPhase.Prefilling; }

        public void Mark(bool reasoning)
        {
            DateTime now = DateTime.UtcNow;
            if (lastDelta is null)
            {
                Prefill += (now - sent).TotalSeconds;
                CurrentPhase = reasoning ? ThreadPhase.Thinking : ThreadPhase.Typing;
            }
            else
            {
                double gap = (now - lastDelta.Value).TotalSeconds;
                if (gap <= 2) { if (reasoning) Thinking += gap; else Typing += gap; }
                CurrentPhase = reasoning ? ThreadPhase.Thinking : ThreadPhase.Typing;
            }
            lastDelta = now;
        }
    }

    // ── Per-turn state ────────────────────────────────────────────────────────
    // Holds all mutable state for one Prompt() call so it can be passed between
    // StreamStep / ProcessStep / ExecuteTools without a parameter explosion.
    private sealed class Turn
    {
        // ── Identity ──────────────────────────────────────────────────────────
        internal readonly Thread        Thread;
        internal readonly PromptOptions Opts;
        internal readonly string        Username;
        internal readonly bool          ChatHidden;
        internal readonly CancellationToken Ct;

        // ── Budgets ───────────────────────────────────────────────────────────
        internal int ThinkBudget;
        internal int RespBudget;
        internal int MaxTokens;       // ThinkBudget + RespBudget — the server's hard ceiling

        // ── System block ─────────────────────────────────────────────────────
        internal string BaseSystem   = "";
        internal string BudgetsBlock = "";
        internal string ThinkSuffix  = "";

        // ── Message list (grows across steps) ────────────────────────────────
        internal readonly List<object> Messages = new();

        // ── Tool state ───────────────────────────────────────────────────────
        internal readonly ToolTurnState                                          ToolTurn;
        internal readonly List<(int Index, string CallId, string Name, string? Path)> ToolResultSlots = new();
        internal readonly List<string>                                           ToolResults = new();
        internal object[]? ToolSchemas;   // rebuilt each step by PrepareStep()

        // ── Step flags (reset each StreamStep) ───────────────────────────────
        internal Dictionary<int, (string Id, string Name, StringBuilder Args)> PendingCalls    = new();
        internal Dictionary<int, string>                                        StreamingMarkers = new();
        internal string?  FinishReason;
        internal (string Id, string Name, string Args, string Error)? EarlyAbort;
        internal int?     RunawayCall;
        internal bool     ContentRunaway;
        internal bool     SteeringRedirect;
        internal bool     ThinkingRedirect;
        internal bool     TextToolLeak;
        internal bool     ResponseContentStarted;
        internal int      ReasoningStartLen;
        internal bool     LastStepToolOnly;
        internal TraceStep? LiveReasoning;
        internal TraceStep? LiveText;

        // ── Counters ─────────────────────────────────────────────────────────
        internal int ToolCallCount;
        internal int ParseFailures;
        internal int ConsecutiveFallbacks;
        internal int TextToolLeakRetries;
        internal int ContinueNudges;
        internal int DegradeEvents;

        // ── Output builders ──────────────────────────────────────────────────
        internal readonly StringBuilder ResponseBuilder  = new();
        internal readonly StringBuilder ContentBuilder   = new();
        internal readonly StringBuilder ReasoningBuilder = new();

        // ── UI card deferred flips ───────────────────────────────────────────
        internal readonly List<(string Active, string Done)> PendingPrefillFlips = new();

        // ── Telemetry ─────────────────────────────────────────────────────────
        internal readonly TurnClock Clock           = new();
        internal readonly Stopwatch  Stopwatch      = Stopwatch.StartNew();
        internal          DateTime   LastProgressLog = DateTime.UtcNow;
        internal int    CompletionTokens;
        internal int    PromptTokens;
        internal int    PrefilledTokens  = -1;
        internal double PrefillTokPerSec;
        internal int    ReasoningChars;
        internal bool   WasThinking;
        internal int    EstimatedTextTokens;
        internal bool   HadImages;

        // ── Response tracking ─────────────────────────────────────────────────
        internal readonly Response        AriResponse;
        internal readonly List<TraceStep> Trace;

        // ── Session recording ─────────────────────────────────────────────────
        // Step index and the previous cumulative counters, so each recorded step reports its own
        // token and clock cost rather than the running total.
        internal SessionRecorder.Run? Rec;
        internal int    RecStep;
        internal int    RecPrevCompletion;
        internal double RecPrevPrefill;
        internal double RecPrevThinking;
        internal double RecPrevTyping;

        // ── Streaming callback ────────────────────────────────────────────────
        internal Func<string, Task>? OnDelta;

        // ── Phase tracking ───────────────────────────────────────────────────
        internal ThreadPhase LastPhase = ThreadPhase.Idle;

        // ── Loop control ─────────────────────────────────────────────────────
        // True while the outer turn loop should keep running (more steps needed).
        internal bool IsStreaming = true;

        internal Turn(Thread thread, PromptOptions opts, Response ariResponse, List<TraceStep> trace,
            ToolTurnState toolTurn, Func<string, Task>? onDelta)
        {
            Thread      = thread;
            Opts        = opts;
            Username    = opts.Username;
            ChatHidden  = opts.ChatHidden;
            Ct          = opts.Ct;
            AriResponse = ariResponse;
            Trace       = trace;
            ToolTurn    = toolTurn;
            OnDelta     = onDelta;
        }
    }

    // One parsed SSE chunk from an open HTTP stream. Advance() reads the next chunk into
    // CurrentDoc; Dispose() closes the stream and signals the server slot is free.
    private sealed class Step : IDisposable
    {
        private readonly Stream        _stream;
        private readonly StreamReader  _reader;
        private readonly Action        _onClose;
        internal         JsonDocument? CurrentDoc;

        internal Step(Stream stream, StreamReader reader, Action onClose)
        {
            _stream  = stream;
            _reader  = reader;
            _onClose = onClose;
        }

        internal async Task<bool> IsStreaming(CancellationToken ct)
        {
            CurrentDoc?.Dispose();
            CurrentDoc = null;
            string? line;
            while ((line = await _reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;
                string payload = line["data: ".Length..];
                if (payload == "[DONE]") return false;
                try   { CurrentDoc = JsonDocument.Parse(payload); return true; }
                catch { continue; }
            }
            return false;
        }

        public void Dispose()
        {
            CurrentDoc?.Dispose();
            _reader.Dispose();
            _stream.Dispose();
            _onClose();
        }
    }

    private async Task<string> Send(Thread thread, string prompt, PromptOptions opts)
    {
        (List<Attachment> threadAtts, List<Attachment> msgAtts) = PrepareUserTurn(thread, prompt, opts);

        prompt = OnPrompt(thread, prompt, opts);
        if (OnPromptPipeline is not null) prompt = OnPromptPipeline(thread, prompt, opts);
        if (!SuppressLog())
            Shared.Logger.LogInformation("[{Agent}] ({Thread}) prompt\n\"{Prompt}\"", Name, thread.Key, prompt);

        // ── Build turn ────────────────────────────────────────────────────────
        Response       ariResponse = new() { Timestamp = DateTime.Now, IsVisible = !opts.ChatHidden };
        List<TraceStep> trace      = new() { new TraceStep { Kind = "prompt", Text = prompt } };
        ariResponse.Trace = trace;
        thread.History.Add(ariResponse);
        thread.RaiseUpdated();
        thread.streamingResponse = ariResponse;
        thread.streamedText      = "";

        Func<string, Task>? userDelta = opts.OnDelta;
        Func<string, Task>? onDelta   = async text => {
            ariResponse.StreamText = text;
            if (!opts.ChatHidden) { thread.streamedText = text; thread.RaiseStreaming(text); }
            if (userDelta is not null) await userDelta(text);
        };

        Turn turn = new(thread, opts, ariResponse, trace, NewTurnState(), onDelta);

        // ── Token budgets ─────────────────────────────────────────────────────
        turn.ThinkBudget = Think ? (opts.ThinkingBudget > 0 ? opts.ThinkingBudget : BudgetThinking) : 0;
        turn.RespBudget  = opts.MaxTokensOverride != 0 ? opts.MaxTokensOverride : BudgetResponse;
        turn.MaxTokens   = turn.RespBudget + turn.ThinkBudget;

        // ── Session record ────────────────────────────────────────────────────
        // Opened here rather than in a pipeline so it covers every agent unconditionally — the
        // dialogue agent, Memory's recall, Context's summariser, Engram's sweep, a Coder sub-thread.
        turn.Rec = SessionRecorder.BeginRun(Name, thread, prompt, turn.MaxTokens, turn.ThinkBudget);

        // ── System block & messages ───────────────────────────────────────────
        int maxChars = BudgetContext > 0 ? (int)(BudgetContext * 3.5) : 0;
        (turn.BaseSystem, turn.BudgetsBlock, turn.ThinkSuffix) = BuildSystemBlock(thread, turn.ThinkBudget, turn.RespBudget, Think);
        turn.Messages.AddRange(BuildMessages(thread, prompt, opts, threadAtts, msgAtts, maxChars, turn.BaseSystem, turn.BudgetsBlock, turn.ThinkSuffix));

        // ── Telemetry bootstrap ───────────────────────────────────────────────
        foreach (object m in turn.Messages)
            turn.EstimatedTextTokens += (ContentOf(m)?.Length ?? 0) / CHARS_PER_TOKEN;
        turn.HadImages = msgAtts.Any(a => a.IsImage) || threadAtts.Any(a => a.IsImage);
        if (thread.liveCallInfo is { } existing)
        {
            existing.EstimatedInputTokens = turn.EstimatedTextTokens;
            existing.OutputTokenLimit     = turn.MaxTokens;
            existing.HadImages            = turn.HadImages;
        }
        else
        {
            thread.liveCallInfo = new LiveCallInfo(Name, thread.Key, turn.EstimatedTextTokens, turn.MaxTokens, BudgetContext, hadImages: turn.HadImages);
        }

        string responseText;
        try
        {
            // A Turn spans the full agent response — multiple LLM round-trips until no tool calls remain.
            // A Step is one LLM request/response cycle; it streams until the model stops generating.
            while (turn.IsStreaming)
            {
                // ── Prepare step ──────────────────────────────────────────────
                // Refresh system message, rebuild tool list (may be exhausted),
                // compact if context is full, inject dynamic context, serialise.
                PrepareStep(turn);
                string json = BuildRequest(turn);

                // ── Stream ────────────────────────────────────────────────────
                using Step? step = await OpenStep(turn, json);
                if (step is null) continue;   // HTTP error recovery; hint injected, retry

                while (await step.IsStreaming(turn.Ct))
                {
                    await ProcessDelta(turn, step);
                    if (turn.ContentRunaway)           break;
                    if (turn.SteeringRedirect)         break;
                    if (turn.ThinkingRedirect)         break;
                    if (turn.EarlyAbort is not null)   break;
                    if (turn.RunawayCall  is not null)  break;
                    if (turn.TextToolLeak)             break;
                }

                // ── Process result & execute tools ────────────────────────────
                await ProcessStep(turn);
                if (turn.IsStreaming && turn.PendingCalls.Count > 0)
                    await ExecuteTools(turn);
            }

            if (FlushPrefillFlips(turn) && turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());

            OnPhaseChange?.Invoke(turn.Thread.Key, ThreadPhase.Idle);
            turn.Stopwatch.Stop();
            responseText = CleanResponse(turn.ContentBuilder, turn.ResponseBuilder);

            responseText = OnResponse(thread, responseText);
            if (OnResponsePipeline is not null) responseText = OnResponsePipeline(thread, responseText);

            FinalizeResponse(thread, prompt, opts, responseText, ariResponse, turn.ReasoningBuilder,
                turn.ToolResults, turn.Clock, turn.Stopwatch.Elapsed.TotalSeconds,
                turn.CompletionTokens, turn.PromptTokens, turn.PrefilledTokens, turn.PrefillTokPerSec,
                turn.MaxTokens, turn.EstimatedTextTokens, turn.HadImages, trace, turn.ResponseBuilder,
                turn.ToolCallCount);
        }
        catch (Exception ex)
        {
            // A cancelled or failed run is the interesting one to replay later — close the record
            // with the reason rather than leaving a run that just stops mid-file.
            SessionRecorder.EndRun(turn.Rec, null, ex);
            throw;
        }

        SessionRecorder.EndRun(turn.Rec, responseText);
        return responseText;
    }

    private void PrepareStep(Turn turn)
    {
        Thread thread = turn.Thread;

        // Refresh system message so per-turn budget numbers are current.
        turn.Messages[0] = new { role = "system", content = turn.BaseSystem + turn.BudgetsBlock + turn.ThinkSuffix };

        if (SupportsCompaction)
            Compact(turn.Messages, turn.ToolResultSlots, BudgetContext, CompactHighPct, CompactLowPct, thread.Snapshots);

        bool toolsExhausted = ToolsCancelled(turn.ToolTurn) || (MaxToolCalls > 0 && turn.ToolCallCount >= MaxToolCalls);
        turn.ToolSchemas = null;
        if (!toolsExhausted && thread.tools.Count > 0)
        {
            List<object> schemas = new();
            foreach (var tool in thread.tools.Values) schemas.Add(tool.Schema);
            turn.ToolSchemas = schemas.ToArray();
        }

        if (thread.liveCallInfo is { } lci)
        {
            long totalChars = 0;
            foreach (object m in turn.Messages) totalChars += ContentOf(m)?.Length ?? 0;
            lci.EstimatedInputTokens = (int)(totalChars / CHARS_PER_TOKEN);
        }

        if (!SuppressLog() && turn.ToolCallCount == 0)
            Shared.Logger.LogInformation("[{Agent}] ({Thread}) {Tools}", Name, thread.Key,
                turn.ToolSchemas is not null ? $"{turn.ToolSchemas.Length} tool(s) available: {string.Join(", ", thread.tools.Keys)}" : "no tools registered");
    }

    private string BuildRequest(Turn turn)
    {
        Thread thread = turn.Thread;

        // Dynamic context is transient — appended for this request only, removed to keep the prefix cache stable.
        string dynamicBlock    = DynamicContext(thread, turn.LastStepToolOnly);
        bool   dynamicInjected = dynamicBlock.Length > 0;
        if (dynamicInjected) turn.Messages.Add(new { role = "user", content = dynamicBlock });

        Dictionary<string, object?> body = BuildRequest(thread, turn.Messages, turn.MaxTokens, turn.ThinkBudget, turn.Opts.ThinkingBudget, turn.ToolSchemas, Think);

        string json = JsonSerializer.Serialize(body);
        if (!SuppressLog())
            Shared.Logger.LogInformation("[{Agent}] ({Thread}) → request (step {Step}): max_tokens={MT}, tools={N}, msgs={Msgs}, think={Think} (et={ET}/budget={B})",
                Name, thread.Key, turn.ToolCallCount,
                body.TryGetValue("max_tokens", out object? mtv) ? mtv : "?",
                turn.ToolSchemas?.Length ?? 0, turn.Messages.Count,
                Think, body.TryGetValue("enable_thinking", out object? etv) ? etv : "unset",
                body.TryGetValue("thinking_budget_tokens", out object? bv) ? bv : "none");

        // Recorded while the dynamic block is still attached — the digests must describe exactly what
        // the server received, including the transient message that breaks the cached prefix.
        turn.RecStep++;
        SessionRecorder.Request(turn.Rec, turn.RecStep, json, turn.Messages, turn.ToolSchemas, dynamicInjected, turn.MaxTokens);

        if (dynamicInjected) turn.Messages.RemoveAt(turn.Messages.Count - 1);

        turn.AriResponse.Data.DebugRequestJson = json;
        return json;
    }

    private async Task<Step?> OpenStep(Turn turn, string json)
    {
        Thread thread = turn.Thread;

        HttpRequestMessage request = new(HttpMethod.Post, $"{Endpoint}/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        turn.Clock.RequestSent();
        if (ThreadPhase.Prefilling != turn.LastPhase)
        {
            turn.LastPhase = ThreadPhase.Prefilling;
            OnPhaseChange?.Invoke(turn.Thread.Key, ThreadPhase.Prefilling);
        }
        Server?.BeginRequest(Name);
        HttpResponseMessage response;
        try   { response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, turn.Ct); }
        catch { Server?.EndRequest(); throw; }

        if (!response.IsSuccessStatusCode)
        {
            Server?.EndRequest();
            string errBody = "";
            try { errBody = await response.Content.ReadAsStringAsync(turn.Ct); } catch { /* ignore */ }

            if (response.StatusCode == System.Net.HttpStatusCode.InternalServerError
                && errBody.Contains("Failed to parse tool call arguments", StringComparison.OrdinalIgnoreCase))
            {
                turn.ParseFailures++;
                Degrade(turn);
                if (turn.ParseFailures > 2)
                    throw new LlmRequestFailedException($"Tool call JSON parse failed {turn.ParseFailures} times in a row — aborting to prevent infinite loop.");
                Shared.Logger.LogWarning("[{Agent}] ({Thread}) Tool call JSON parse failure — injecting recovery hint.", Name, thread.Key);
                string hint = "One of your tool call arguments contained characters (such as unescaped double-quotes in XML/XAML content) that made the JSON invalid. " +
                              "Please retry: escape all double-quotes inside string values as \\\" and avoid raw newlines inside JSON strings.";
                turn.Messages.Add(new { role = "user", content = hint });
                return null;
            }

            throw new LlmRequestFailedException($"LLM request failed with status: {response.StatusCode}" + (errBody.Length > 0 ? $" — {errBody[..Math.Min(errBody.Length, 300)]}" : ""));
        }

        // Reset per-step flags before handing the stream to the caller.
        turn.PendingCalls     = new();
        turn.StreamingMarkers = new();
        turn.FinishReason     = null;
        turn.EarlyAbort       = null;
        turn.RunawayCall      = null;
        turn.ContentRunaway   = false;
        turn.SteeringRedirect = false;
        turn.ThinkingRedirect = false;
        turn.TextToolLeak     = false;
        turn.ResponseContentStarted = false;
        turn.ReasoningStartLen      = turn.ReasoningBuilder.Length;
        turn.LiveReasoning          = null;
        turn.LiveText               = null;
        turn.ResponseBuilder.Clear();

        Stream       stream = await response.Content.ReadAsStreamAsync(turn.Ct);
        StreamReader reader = new(stream);
        return new Step(stream, reader, () => Server?.EndRequest());
    }

    private async Task ProcessDelta(Turn turn, Step step)
    {
        Thread      thread = turn.Thread;
        JsonElement root   = step.CurrentDoc!.RootElement;

        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            turn.CompletionTokens += usage.TryGetProperty("completion_tokens", out JsonElement ctEl) ? ctEl.GetInt32() : 0;
            turn.PromptTokens      = usage.TryGetProperty("prompt_tokens",     out JsonElement ptEl) ? ptEl.GetInt32() : 0;
        }

        // prompt_n = tokens actually re-prefilled; the rest is a KV cache hit.
        if (root.TryGetProperty("timings", out JsonElement timings))
        {
            if (timings.TryGetProperty("prompt_n",          out JsonElement pnEl)) turn.PrefilledTokens  = pnEl.GetInt32();
            if (timings.TryGetProperty("prompt_per_second", out JsonElement ppsEl) && ppsEl.ValueKind == JsonValueKind.Number)
                turn.PrefillTokPerSec = ppsEl.GetDouble();
        }

        if (!root.TryGetProperty("choices", out JsonElement choices) || choices.GetArrayLength() == 0) return;

        JsonElement choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out JsonElement frEl) && frEl.ValueKind != JsonValueKind.Null)
            turn.FinishReason = frEl.GetString();

        JsonElement delta = choice.GetProperty("delta");

        // Wall-clock bucketing: the first delta of a request closes its prefill window; after
        // that each delta ticks the thinking or typing clock (stalls tick neither — see TurnClock).
        bool reasoningDelta = delta.TryGetProperty("reasoning_content", out JsonElement rcProbe)
            && rcProbe.ValueKind == JsonValueKind.String && (rcProbe.GetString()?.Length ?? 0) > 0;
        turn.Clock.Mark(reasoningDelta);

        ThreadPhase phase = turn.Clock.CurrentPhase;
        if (phase != turn.LastPhase)
        {
            turn.LastPhase = phase;
            OnPhaseChange?.Invoke(turn.Thread.Key, phase);
        }

        if (!SuppressLog())
        {
            DateTime now = DateTime.UtcNow;
            if ((now - turn.LastProgressLog).TotalSeconds >= 3)
            {
                turn.LastProgressLog = now;
                int argChars = 0;
                List<string> callNames = new();
                foreach (var call in turn.PendingCalls.Values) { argChars += call.Args.Length; callNames.Add(call.Name); }
                string tail = turn.ResponseBuilder.Length > 0
                    ? turn.ResponseBuilder.ToString()
                    : (turn.PendingCalls.Count > 0 ? turn.PendingCalls.Values.Last().Args.ToString() : "");
                if (tail.Length > 120) tail = tail[^120..];
                Shared.Logger.LogInformation("[{Agent}] ({Thread}) … decoding: {N} native call(s) [{Names}], {AC} arg chars, {CC} content chars | …{Tail}",
                    Name, thread.Key, turn.PendingCalls.Count, string.Join(",", callNames),
                    argChars, turn.ResponseBuilder.Length, tail.Replace("\n", "\\n"));

                if (Runaway.IsSpiral(turn.ResponseBuilder, out char domChar, out double ratio))
                {
                    Shared.Logger.LogWarning("[{Agent}] ({Thread}) content runaway: '{Char}' is {Pct}% of recent output — aborting generation.",
                        Name, thread.Key, domChar == '\\' ? "\\\\" : domChar.ToString(), (int)(ratio * 100));
                    turn.ContentRunaway = true;
                    return;
                }
            }
        }

        if (delta.TryGetProperty("reasoning_content", out JsonElement reasoning))
        {
            string? thinkDelta = reasoning.GetString();
            if (!string.IsNullOrEmpty(thinkDelta) && !turn.WasThinking)
            {
                if (!Think) Shared.Logger.LogWarning("[{Agent}] ({Thread}) thinking chain detected — <|think_off|> may not be working.", Name, thread.Key);
                else        Shared.Logger.LogInformation("[{Agent}] ({Thread}) reasoning engaged (thinking on).", Name, thread.Key);
                turn.WasThinking = true;
            }
            if (!string.IsNullOrEmpty(thinkDelta)) { turn.ReasoningChars += thinkDelta.Length; turn.ReasoningBuilder.Append(thinkDelta); }
            if (turn.LiveReasoning is null) { turn.LiveReasoning = new TraceStep { Kind = "reasoning", Text = "" }; turn.Trace.Add(turn.LiveReasoning); }
            turn.LiveReasoning.Text = turn.ReasoningBuilder.ToString(turn.ReasoningStartLen, turn.ReasoningBuilder.Length - turn.ReasoningStartLen);
            if (!turn.ChatHidden) thread.RaiseStreaming(thread.streamedText);

            if (!string.IsNullOrEmpty(thinkDelta))
            {
                string? agentThinkRedirect = OnThinkingDelta(thread, thinkDelta);
                string? finalThinkRedirect = OnThinkingDeltaPipeline?.Invoke(thread, agentThinkRedirect ?? thinkDelta) ?? agentThinkRedirect;
                if (finalThinkRedirect is not null)
                {
                    turn.Messages.Add(new { role = "assistant", content = "" });
                    turn.Messages.Add(new { role = "user", content = finalThinkRedirect });
                    turn.ThinkingRedirect = true;
                }
            }
            return;
        }

        if (delta.TryGetProperty("tool_calls", out JsonElement toolCallsEl))
        {
            if (turn.ResponseBuilder.Length > 0)
            {
                string preText = turn.ResponseBuilder.ToString().TrimEnd();
                bool isLeakedToolCall = preText.Contains("<tool_call>") || preText.Contains("<function=")
                    || thread.tools.Keys.Any(k => preText.StartsWith(k, StringComparison.OrdinalIgnoreCase));
                if (!isLeakedToolCall && preText.Length > 0)
                {
                    turn.ContentBuilder.Append(preText + "\n");
                    if (turn.LiveText is not null) { turn.Trace.Remove(turn.LiveText); turn.LiveText = null; }
                    turn.Trace.Add(new TraceStep { Kind = "text", Text = preText });
                    if (!SuppressLog()) Shared.Logger.LogInformation("[{Agent}] ({Thread}) \"{Text}\"", Name, thread.Key, preText);
                }
                turn.ResponseBuilder.Clear();
                if (turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());
            }

            foreach (JsonElement tc in toolCallsEl.EnumerateArray())
            {
                int index = tc.GetProperty("index").GetInt32();

                if (tc.TryGetProperty("id", out JsonElement idEl))
                {
                    string id   = idEl.GetString() ?? string.Empty;
                    string name = tc.TryGetProperty("function", out JsonElement fn) && fn.TryGetProperty("name", out JsonElement nameEl)
                        ? nameEl.GetString() ?? string.Empty : string.Empty;
                    turn.PendingCalls[index] = (id, name, new StringBuilder());
                    turn.ConsecutiveFallbacks = 0;
                }

                if (tc.TryGetProperty("function", out JsonElement funcEl) &&
                    funcEl.TryGetProperty("arguments", out JsonElement argsEl))
                {
                    string? argsDelta = argsEl.GetString();
                    if (!string.IsNullOrEmpty(argsDelta) && turn.PendingCalls.TryGetValue(index, out (string Id, string Name, StringBuilder Args) call))
                    {
                        call.Args.Append(argsDelta);

                        if (turn.RunawayCall is null && Runaway.IsToolLeak(call.Args.ToString()))
                        {
                            turn.RunawayCall = index;
                            Shared.Logger.LogWarning("[{Agent}] ({Thread}) native tool-call runaway ({Tool}, {Len} arg chars — text-format leak) — aborting generation and salvaging.", Name, thread.Key, call.Name, call.Args.Length);
                            return;
                        }

                        if (turn.EarlyAbort is null)
                        {
                            string partialArgs = call.Args.ToString();
                            string? abortMsg = null;
                            if (thread.tools.TryGetValue(call.Name, out var streamTool))
                                abortMsg = streamTool.StreamingPreCheck?.Invoke(partialArgs);
                            if (abortMsg is null)
                                abortMsg = OnToolStreaming(thread, turn.ToolTurn, call.Name, partialArgs);
                            if (abortMsg is null && OnToolStreamingPipeline is not null)
                                abortMsg = OnToolStreamingPipeline(thread, turn.ToolTurn, call.Name, partialArgs);
                            if (abortMsg is not null)
                                turn.EarlyAbort = (call.Id, call.Name, partialArgs, abortMsg);
                        }

                        if (thread.tools.TryGetValue(call.Name, out var liveTool) && liveTool.StreamingDisplay is not null)
                        {
                            string? newMarker = liveTool.StreamingDisplay(call.Args.ToString());
                            if (newMarker != null)
                            {
                                if (turn.StreamingMarkers.TryGetValue(index, out string? prevMarker))
                                {
                                    if (newMarker != prevMarker)
                                    {
                                        ReplaceInBuilder(turn.ContentBuilder, prevMarker, newMarker);
                                        turn.StreamingMarkers[index] = newMarker;
                                        if (turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());
                                    }
                                }
                                else
                                {
                                    turn.ContentBuilder.Append(newMarker);
                                    turn.StreamingMarkers[index] = newMarker;
                                    if (turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());
                                }
                            }
                        }

                        if (turn.EarlyAbort is not null || turn.RunawayCall is not null) return;
                    }
                }
            }
            return;
        }

        if (!delta.TryGetProperty("content", out JsonElement contentEl)) return;
        string? deltaText = contentEl.GetString();
        if (string.IsNullOrEmpty(deltaText)) return;

        if (!turn.ResponseContentStarted)
        {
            string? agentRedirect = OnStreamingDelta(thread, deltaText);
            string? finalRedirect = OnStreamingDeltaPipeline?.Invoke(thread, agentRedirect ?? deltaText) ?? agentRedirect;
            if (finalRedirect is not null)
            {
                string capturedThink = turn.ReasoningBuilder.Length > turn.ReasoningStartLen
                    ? "<think>\n" + turn.ReasoningBuilder.ToString(turn.ReasoningStartLen, turn.ReasoningBuilder.Length - turn.ReasoningStartLen).TrimEnd() + "\n</think>\n"
                    : "";
                turn.Messages.Add(new { role = "assistant", content = capturedThink });
                turn.Messages.Add(new { role = "user", content = string.IsNullOrWhiteSpace(finalRedirect)
                    ? "[Keep thinking — the user is still speaking. Do not respond yet.]"
                    : $"[Keep thinking — the user is still speaking. Do not respond yet.]\nFurther transcript — {turn.Username}: {finalRedirect}" });
                turn.ReasoningStartLen = turn.ReasoningBuilder.Length;
                turn.SteeringRedirect  = true;
                return;
            }
        }
        turn.ResponseContentStarted = true;
        deltaText = deltaText
            .Replace("<|think_off|>", "")
            .Replace("<|think_on|>",  "")
            .Replace("<|tool_code_start|>", "")
            .Replace("<|tool_code_end|>",   "")
            .Replace("<|tool_call|>",       "");
        if (string.IsNullOrEmpty(deltaText)) return;

        turn.ResponseBuilder.Append(deltaText);
        if (thread.LiveCall is { } lc) lc.EstimatedOutputTokens = turn.ResponseBuilder.Length / CHARS_PER_TOKEN;
        if (!turn.TextToolLeak && turn.PendingCalls.Count == 0 && turn.TextToolLeakRetries < 3
            && turn.ResponseBuilder.Length >= 3
            && ToolCallParser.IsTextToolCall(turn.ResponseBuilder.ToString(), thread.tools.Keys))
        {
            turn.TextToolLeak = true;
            return;
        }
        if (turn.OnDelta is not null)
        {
            const string AriPrefix = "ARI: ";
            string accumulated = turn.ResponseBuilder.ToString();
            string visible = accumulated.Length < AriPrefix.Length
                ? (accumulated.StartsWith(AriPrefix[..accumulated.Length], StringComparison.OrdinalIgnoreCase) ? "" : accumulated)
                : (accumulated.StartsWith(AriPrefix, StringComparison.OrdinalIgnoreCase) ? accumulated[AriPrefix.Length..] : accumulated);
            if (turn.LiveText is null && visible.Trim().Length > 0) { turn.LiveText = new TraceStep { Kind = "text", Text = "" }; turn.Trace.Add(turn.LiveText); }
            if (turn.LiveText is not null) turn.LiveText.Text = visible;
            await turn.OnDelta(turn.ContentBuilder.ToString() + visible);
        }
    }

    /// <summary>Records the step the model just finished, before ProcessStep's recovery branches start
    /// rewriting the builders. Completion tokens and the clock buckets accumulate across a turn, so they
    /// are differenced here — a recorded step reports its own cost, and the file sums back to the turn.</summary>
    private static void RecordStepResponse(Turn turn)
    {
        if (turn.Rec is null) return;

        string? reasoning = turn.ReasoningBuilder.Length > turn.ReasoningStartLen
            ? turn.ReasoningBuilder.ToString(turn.ReasoningStartLen, turn.ReasoningBuilder.Length - turn.ReasoningStartLen)
            : null;

        SessionRecorder.Response(
            turn.Rec, turn.RecStep,
            turn.ResponseBuilder.Length > 0 ? turn.ResponseBuilder.ToString() : null,
            reasoning,
            turn.FinishReason,
            turn.PromptTokens,
            turn.PrefilledTokens,
            turn.CompletionTokens - turn.RecPrevCompletion,
            turn.Clock.Prefill  - turn.RecPrevPrefill,
            turn.Clock.Thinking - turn.RecPrevThinking,
            turn.Clock.Typing   - turn.RecPrevTyping,
            turn.PrefillTokPerSec,
            turn.PendingCalls.Values.Select(c => c.Name));

        turn.RecPrevCompletion = turn.CompletionTokens;
        turn.RecPrevPrefill    = turn.Clock.Prefill;
        turn.RecPrevThinking   = turn.Clock.Thinking;
        turn.RecPrevTyping     = turn.Clock.Typing;
    }

    private async Task ProcessStep(Turn turn)
    {
        Thread thread = turn.Thread;

        RecordStepResponse(turn);

        if (turn.ReasoningBuilder.Length > turn.ReasoningStartLen)
        {
            if (turn.LiveReasoning is null) { turn.LiveReasoning = new TraceStep { Kind = "reasoning" }; turn.Trace.Add(turn.LiveReasoning); }
            turn.LiveReasoning.Text = turn.ReasoningBuilder.ToString(turn.ReasoningStartLen, turn.ReasoningBuilder.Length - turn.ReasoningStartLen);
        }
        if (turn.LiveText is not null) { turn.Trace.Remove(turn.LiveText); turn.LiveText = null; }

        // Reinject reasoning as <think> into the assistant turn so the next step doesn't re-derive it.
        string stepThink = turn.ReasoningBuilder.Length > turn.ReasoningStartLen
            ? "<think>\n" + turn.ReasoningBuilder.ToString(turn.ReasoningStartLen, turn.ReasoningBuilder.Length - turn.ReasoningStartLen).Trim() + "\n</think>\n"
            : "";

        if (LogReasoning && turn.ReasoningBuilder.Length > turn.ReasoningStartLen)
        {
            try
            {
                string rf = System.IO.Path.Combine(Paths.Logs, $"reasoning-{Name}.log");
                System.IO.File.AppendAllText(rf,
                    $"\n===== [{DateTime.Now:HH:mm:ss}] {thread.Key} =====\n"
                    + turn.ReasoningBuilder.ToString(turn.ReasoningStartLen, turn.ReasoningBuilder.Length - turn.ReasoningStartLen).Trim()
                    + "\n");
            }
            catch { /* tracing must never break a turn */ }
        }

        if (!SuppressLog())
        {
            int doneArgChars = 0;
            var doneNames    = new List<string>();
            foreach (var call in turn.PendingCalls.Values) { doneArgChars += call.Args.Length; doneNames.Add(call.Name); }
            Shared.Logger.LogInformation("[{Agent}] ({Thread}) ← stream done: finish={FR}, completion_tokens={CT}, reasoning_chars={RC}, {PC} native call(s) [{Names}], {CC} content chars, {AC} arg chars",
                Name, thread.Key, turn.FinishReason ?? "null", turn.CompletionTokens, turn.ReasoningChars, turn.PendingCalls.Count,
                string.Join(",", doneNames), turn.ResponseBuilder.Length, doneArgChars);
            if (turn.ResponseBuilder.Length > 0)
            {
                string snip = turn.ResponseBuilder.ToString();
                Shared.Logger.LogInformation("[{Agent}] ({Thread}) ← content: {Snip}",
                    Name, thread.Key, (snip.Length > 400 ? snip[..400] + "…" : snip).Replace("\n", "\\n"));
            }
        }

        turn.LastStepToolOnly = turn.ResponseBuilder.Length == 0 && turn.PendingCalls.Count > 0;

        if (turn.ContentRunaway)
        {
            turn.ResponseBuilder.Clear();
            turn.ContentBuilder.Append("\n\n_Stopped — the model's output ran away repeating characters._");
            if (turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());
            turn.IsStreaming = false;
            return;
        }

        if (turn.SteeringRedirect)
        {
            Shared.Logger.LogInformation("[{Agent}] ({Thread}) streaming delta hook: redirected to continue thinking.", Name, thread.Key);
            return;
        }

        if (turn.ThinkingRedirect)
        {
            Shared.Logger.LogInformation("[{Agent}] ({Thread}) thinking delta hook: interrupted reasoning with redirect.", Name, thread.Key);
            return;
        }

        if (turn.TextToolLeak)
        {
            turn.TextToolLeakRetries++;
            Shared.Logger.LogWarning("[{Agent}] ({Thread}) text-format tool call detected (retry {N}/3) — steering model to emit natively.", Name, thread.Key, turn.TextToolLeakRetries);
            turn.ResponseBuilder.Clear();
            turn.Messages.Add(new { role = "assistant", content = "" });
            turn.Messages.Add(new { role = "user",      content = "[System] You wrote a tool call as plain text. Never output tool names or <tool_call> XML in prose — use the native function-call API." });
            return;
        }

        if (turn.EarlyAbort is not null)
        {
            var (aId, aName, aArgs, aErr) = turn.EarlyAbort.Value;
            string? aPath    = ToolCallParser.TryExtractJsonString(aArgs, "path");
            string  safeArgs = JsonSerializer.Serialize(new { path = aPath ?? "" });

            if (turn.ResponseBuilder.Length > 0)
            {
                string preText = turn.ResponseBuilder.ToString().TrimEnd();
                if (preText.Length > 0) turn.ContentBuilder.Append(preText + "\n");
            }

            turn.Messages.Add(new { role = "assistant", tool_calls = new[]
                { new { id = aId, type = "function", function = new { name = aName, arguments = safeArgs } } } });
            turn.Messages.Add(new { role = "tool", tool_call_id = aId, name = aName, content = aErr });
            if (thread.liveCallInfo is { } lcAbort) lcAbort.EstimatedInputTokens += aErr.Length / CHARS_PER_TOKEN;

            string aLabel = aPath is not null ? System.IO.Path.GetFileName(aPath.Trim('"', '\'', ' ', '\\')) : "";
            turn.ContentBuilder.Append($"<!--ari-tool-error:{aName}:{aLabel}:{ToolCallParser.EscapeLabel(aErr)}-->");
            if (turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());

            turn.ToolCallCount++;
            Degrade(turn);
            return;
        }

        if (turn.PendingCalls.Count == 0 && turn.ResponseBuilder.Length > 0)
        {
            string rawResponse = turn.ResponseBuilder.ToString();
            if (rawResponse.Contains("<|tool_code_start|>") || rawResponse.Contains("<|tool_call|>"))
            {
                turn.ConsecutiveFallbacks++;
                Degrade(turn);
                if (turn.ConsecutiveFallbacks > 3)
                    throw new LlmRequestFailedException($"Model stuck in tool_code_start fallback loop ({turn.ConsecutiveFallbacks} consecutive) — aborting.");
                Shared.Logger.LogWarning("[{Agent}] ({Thread}) model used <|tool_code_start|> format — cannot parse, injecting correction.", Name, thread.Key);
                turn.Messages.Add(new { role = "assistant", content = rawResponse.Replace("<|tool_code_start|>", "").Replace("<|tool_code_end|>", "").Replace("<|tool_call|>", "").Trim() });
                turn.Messages.Add(new { role = "user", content = "[System: Your last response contained tool call markers (<|tool_code_start|> or <|tool_call|>) with no parseable arguments. Do not use these markers. Issue tool calls using only the proper JSON function-call format.]" });
                turn.ResponseBuilder.Clear();
                return;
            }
        }

        if (turn.PendingCalls.Count > 0 && (turn.FinishReason == "tool_calls" || turn.FinishReason == "stop" || turn.FinishReason == null))
        {
            // Sanitise args before execution — repair malformed JSON, strip think leaks, salvage runaway args.
            foreach (var key in turn.PendingCalls.Keys)
            {
                var (id, name, args) = turn.PendingCalls[key];
                string original = args.ToString();
                string raw      = original;
                if (Runaway.IsToolLeak(raw))
                {
                    raw = ToolCallParser.SalvageNativeArgs(raw);
                    Shared.Logger.LogInformation("[{Agent}] ({Thread}) salvaged runaway args for '{Tool}' → {Args}", Name, thread.Key, name, raw);
                }
                string stripped = ToolCallParser.StripThinkLeaks(raw);
                string repaired = ToolCallParser.RepairArgs(stripped);
                if (stripped != raw)  Shared.Logger.LogWarning("[{Agent}] ({Thread}) Stripped <think> leakage from args for tool '{Tool}'.", Name, thread.Key, name);
                if (repaired != stripped) Shared.Logger.LogWarning("[{Agent}] ({Thread}) Repaired malformed JSON args for tool '{Tool}'.", Name, thread.Key, name);
                if (repaired != original) turn.PendingCalls[key] = (id, name, new StringBuilder(repaired));
            }

            turn.ToolCallCount += turn.PendingCalls.Count;

            var orderedCalls = new List<KeyValuePair<int, (string Id, string Name, StringBuilder Args)>>(turn.PendingCalls);
            orderedCalls.Sort((a, b) => a.Key.CompareTo(b.Key));

            var toolCallList = new List<object>();
            foreach (var kv in orderedCalls)
            {
                string args = ToolCallParser.TrimArgs(kv.Value.Name, kv.Value.Args.ToString());
                toolCallList.Add(new { id = kv.Value.Id, type = "function", function = new { name = kv.Value.Name, arguments = args } });
            }

            if (stepThink.Length > 0)
                turn.Messages.Add(new { role = "assistant", content = stepThink, tool_calls = toolCallList });
            else
                turn.Messages.Add(new { role = "assistant", tool_calls = toolCallList });

            return;
        }

        // ── No tool calls — check for step injection, nudges, or turn end ────
        {
            string stepText = turn.ResponseBuilder.ToString();
            bool hadTools   = turn.PendingCalls.Count > 0;
            string? stepInjection = OnStepComplete(thread, stepText, hadTools);
            if (stepInjection is null && OnStepCompletePipeline is not null)
                stepInjection = OnStepCompletePipeline(thread, stepText, hadTools);
            if (stepInjection is not null)
            {
                turn.Messages.Add(new { role = "user", content = stepInjection });
                turn.ResponseBuilder.Clear();
                return;
            }
        }

        bool toolsStillAvailable = !ToolsCancelled(turn.ToolTurn) && !(MaxToolCalls > 0 && turn.ToolCallCount >= MaxToolCalls);
        if (turn.PendingCalls.Count == 0 && toolsStillAvailable && turn.ContinueNudges < 2)
        {
            string? nudge = NeedsNudge(thread, turn.ResponseBuilder.ToString(), turn.ReasoningBuilder, turn.ReasoningStartLen);
            if (nudge is not null)
            {
                string? finalNudge = OnNudge(thread, nudge);
                if (finalNudge is null && OnNudgePipeline is not null) finalNudge = OnNudgePipeline(thread, nudge);
                else if (finalNudge is not null && OnNudgePipeline is not null) finalNudge = OnNudgePipeline(thread, finalNudge) ?? finalNudge;
                if (finalNudge is not null)
                {
                    turn.ContinueNudges++;
                    turn.Messages.Add(new { role = "user", content = finalNudge });
                    turn.ResponseBuilder.Clear();
                    return;
                }
            }
        }

        turn.IsStreaming = false;
    }

    private async Task ExecuteTools(Turn turn)
    {
        Thread thread = turn.Thread;

        OnBatchStart(turn.ToolTurn);

        HashSet<string> readOnlyTools = new(StringComparer.OrdinalIgnoreCase)
            { "read_file", "search_files", "list_directory", "find_files", "search_brain" };
        Dictionary<int, Task<string>> prelaunched = new();
        if (turn.PendingCalls.Count > 1)
            foreach (var (idx, c) in turn.PendingCalls)
                if (readOnlyTools.Contains(c.Name) && thread.tools.TryGetValue(c.Name, out var roTool))
                    prelaunched[idx] = roTool.Execute(c.Args.ToString());

        bool productiveBatch   = false;
        bool batchRevealedCard = false;

        foreach (var (callIndex, call) in turn.PendingCalls)
        {
            string argsJson = call.Args.ToString();
            string result;

            turn.Trace.Add(new TraceStep { Kind = "tool_call", Name = call.Name, Args = argsJson });
            SessionRecorder.ToolCall(turn.Rec, turn.RecStep, call.Id, call.Name, argsJson);

            string? guard = null;
            bool toolFound = thread.tools.TryGetValue(call.Name, out var tool);
            if (toolFound)
                guard = tool.PreCheck?.Invoke(argsJson);
            if (guard is null)
                guard = OnToolCall(thread, turn.ToolTurn, call.Name, call.Id, argsJson);
            if (guard is null && OnToolCallPipeline is not null)
                guard = OnToolCallPipeline(thread, call.Name, argsJson);

            if (guard is not null)
            {
                result = guard;
            }
            else if (toolFound)
            {
                string? activeMarker = null;
                if (turn.StreamingMarkers.TryGetValue(callIndex, out string? prevStreamMarker))
                    activeMarker = prevStreamMarker;
                else if (tool.Display is not null)
                {
                    // Flush deferred read/preview card flips together with the new card in one delta frame.
                    // Guarded/deduped calls skip this branch entirely, so no flip fires without a real card.
                    if (!batchRevealedCard)
                    {
                        batchRevealedCard = true;
                        FlushPrefillFlips(turn);
                    }
                    activeMarker = tool.Display(argsJson);
                    turn.ContentBuilder.Append(activeMarker);
                    if (turn.OnDelta is not null)
                    {
                        string payload = turn.ContentBuilder.ToString();
                        Shared.Logger.LogInformation("[{Agent}] ({Thread}) reveal-send len={Len} tail={Tail}",
                            Name, thread.Key, payload.Length, payload.Length > 400 ? payload[^400..] : payload);
                        await turn.OnDelta(payload);
                    }
                }

                thread.ToolDisplaySink = async chunk =>
                {
                    turn.ContentBuilder.Append(chunk);
                    if (turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());
                };
                try
                {
                    result = prelaunched.TryGetValue(callIndex, out Task<string>? pre)
                        ? await pre
                        : await tool.Execute(argsJson);
                }
                finally { thread.ToolDisplaySink = null; }

                result = OnToolResult(thread, turn.ToolTurn, call.Name, argsJson, result);
                if (OnToolResultPipeline is not null)
                    result = OnToolResultPipeline(thread, call.Name, result);

                // read_file auto-diverted to preview: relabel card and defer flip until next prefill.
                if (activeMarker is not null && call.Name == "read_file" && result.StartsWith("[preview:", StringComparison.Ordinal))
                {
                    string pf = "";
                    try { using JsonDocument pvd = JsonDocument.Parse(argsJson); pf = System.IO.Path.GetFileName((pvd.RootElement.TryGetProperty("path", out JsonElement ppe) ? ppe.GetString() : null)?.Trim('"', '\'', ' ') ?? ""); }
                    catch { /* ignore */ }
                    string pfEsc     = pf.Replace("--", "&#45;&#45;");
                    string startPrev = $"<!--ari-tool-start:preview_file:{pfEsc}-->";
                    string donePrev  = $"<!--ari-tool-done:preview_file:{pfEsc}-->";
                    ReplaceInBuilder(turn.ContentBuilder, activeMarker, startPrev);
                    if (turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());
                    Shared.Logger.LogInformation("[{Agent}] ({Thread}) defer-prefill-flip (auto-divert): {Done}", Name, thread.Key, donePrev);
                    turn.PendingPrefillFlips.Add((startPrev, donePrev));
                    activeMarker = null;
                }

                if (activeMarker is not null && !ToolCallParser.IsError(result))
                {
                    Card? doneCard = null;
                    foreach (object block in ContentBlock.Parse(activeMarker))
                    {
                        if (block is Card c) { doneCard = c; break; }
                    }
                    if (doneCard is not null)
                    {
                        doneCard.Flip();
                        string done = doneCard.Render();
                        if (!string.Equals(done, activeMarker, StringComparison.Ordinal))
                        {
                            if (call.Name is "read_file" or "preview_file")
                            {
                                Shared.Logger.LogInformation("[{Agent}] ({Thread}) defer-prefill-flip: {Done}", Name, thread.Key, done);
                                turn.PendingPrefillFlips.Add((activeMarker, done));
                            }
                            else
                            {
                                ReplaceInBuilder(turn.ContentBuilder, activeMarker, done);
                                if (turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());
                            }
                        }
                    }
                }

                if (ToolCallParser.IsError(result))
                    Shared.Logger.LogError("[{Agent}] ({Thread}) Tool '{Tool}' failed: {Error}", Name, thread.Key, call.Name, result);
                else if (tool.DisplayAfter is not null)
                {
                    turn.ContentBuilder.Append(tool.DisplayAfter(argsJson));
                    if (turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());
                }
            }
            else
            {
                result = $"[Error: tool '{call.Name}' is not registered]";
                Shared.Logger.LogError("[{Agent}] ({Thread}) Model called unknown tool '{Tool}'", Name, thread.Key, call.Name);
            }

            if (call.Name == "plan_proposed")
            {
                string planText = (ToolCallParser.TryExtractJsonString(argsJson, "payload") ?? "").Trim();
                if (planText.Length > 0) turn.ContentBuilder.Append("\n\n" + planText + "\n");
                turn.ContentBuilder.Append("<!--ari-plan-proposed-->");
                productiveBatch = true;
                if (turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());
            }
            else if (call.Name == "replan")
            {
                turn.ContentBuilder.Append("<!--ari-tool-mode:replan:Returning to planning-->");
                productiveBatch = true;
                if (turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());
            }
            else if (result.StartsWith("[System:", StringComparison.Ordinal) || ToolCallParser.IsError(result))
            {
                string label = "";
                try
                {
                    using JsonDocument lDoc = JsonDocument.Parse(argsJson);
                    string lp = lDoc.RootElement.TryGetProperty("path",    out var lpe)  ? lpe.GetString()  ?? "" :
                                lDoc.RootElement.TryGetProperty("pattern", out var lpte) ? lpte.GetString() ?? "" : "";
                    label = System.IO.Path.GetFileName(lp.Trim('"', '\'', ' ', '\\'));
                }
                catch { /* ignore */ }
                turn.ContentBuilder.Append($"<!--ari-tool-error:{call.Name}:{label}:{ToolCallParser.EscapeLabel(result)}-->");
                if (turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());
            }

            turn.ToolResults.Add(result);
            turn.Trace.Add(new TraceStep { Kind = "tool_result", Name = call.Name, Text = result });
            SessionRecorder.ToolResult(turn.Rec, turn.RecStep, call.Id, call.Name, result);
            // Guard nags and errors don't count as progress — only real content/mutations do.
            if (!result.StartsWith("[System:", StringComparison.OrdinalIgnoreCase) && !ToolCallParser.IsError(result))
                productiveBatch = true;

            int addedIndex = turn.Messages.Count;
            turn.Messages.Add(new { role = "tool", tool_call_id = call.Id, name = call.Name, content = result });
            // Capture the path for read_file/preview_file — if this output later gets stubbed by compaction,
            // we need to clear the file's read-dedup ledger so the model can re-read it without being blocked.
            string? readPath = null;
            if (call.Name is "read_file" or "preview_file")
            {
                try
                {
                    using JsonDocument pDoc = JsonDocument.Parse(argsJson);
                    if (pDoc.RootElement.TryGetProperty("path", out var pe)) readPath = pe.GetString();
                }
                catch { /* ignore — dedup exemption just won't fire for this call */ }
            }
            turn.ToolResultSlots.Add((addedIndex, call.Id, call.Name, readPath));
            if (thread.liveCallInfo is { } lc) lc.EstimatedInputTokens += result.Length / CHARS_PER_TOKEN;
            OnToolAdded(turn.ToolTurn, turn.Messages, call.Name, call.Id, argsJson, result, addedIndex);
        }

        turn.ContentBuilder.Append("<!--ari-batch-end-->");
        if (turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());

        if (thread.EndTurnNow)
        {
            thread.EndTurnNow = false;
            turn.IsStreaming = false;
            return;
        }

        if (ShouldBreak(thread, turn.ToolTurn, productiveBatch))
        {
            turn.ContentBuilder.Append("\n\n_Stopped — repeated tool calls were not making progress._");
            if (turn.OnDelta is not null) await turn.OnDelta(turn.ContentBuilder.ToString());
            turn.IsStreaming = false;
        }
    }

    private void Degrade(Turn turn)
    {
        if (++turn.DegradeEvents >= MAX_DEGRADE_EVENTS)
            throw new LlmRequestFailedException(
                $"Tool-call formatting failed {turn.DegradeEvents} times this turn — stopping to avoid a spiral. Any changes already applied are kept.");
    }

    private bool FlushPrefillFlips(Turn turn,
        [System.Runtime.CompilerServices.CallerMemberName] string _ = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0)
    {
        if (turn.PendingPrefillFlips.Count == 0) return false;
        var flipDones = new List<string>();
        foreach (var flip in turn.PendingPrefillFlips) flipDones.Add(flip.Done);
        Shared.Logger.LogInformation("[{Agent}] ({Thread}) flush-prefill-flips @L{Line}: {Count} card(s) — {Cards}",
            Name, turn.Thread.Key, callerLine, turn.PendingPrefillFlips.Count, string.Join(", ", flipDones));
        foreach ((string active, string done) in turn.PendingPrefillFlips)
            ReplaceInBuilder(turn.ContentBuilder, active, done);
        turn.PendingPrefillFlips.Clear();
        return true;
    }

    // ── Extracted send phases ─────────────────────────────────────────────────

    private (List<Attachment> threadAtts, List<Attachment> msgAtts) PrepareUserTurn(
        Thread thread, string prompt, PromptOptions opts)
    {
        thread.LastMessageAt = DateTime.UtcNow;
        thread.OnUserSend();

        if (thread.ariRepliedAt != DateTime.MinValue)
        {
            int sampleWindow = MemoryLimit > 0 ? MemoryLimit : AVERAGE_RESPONSE_WINDOW;
            thread.responseSamples.Add(DateTime.UtcNow - thread.ariRepliedAt);
            if (thread.responseSamples.Count > sampleWindow)
                thread.responseSamples.RemoveAt(0);
            thread.ariRepliedAt = DateTime.MinValue;
        }

        List<Attachment> threadAtts = thread.SnapshotThreadAttachments();
        List<Attachment> msgAtts;
        if (opts.UserMessagePreadded)
        {
            Prompt? lastMsg = null;
            foreach (object item in thread.History)
            {
                if (item is Prompt p) lastMsg = p;
            }
            msgAtts = lastMsg?.Attachments?.ToList() ?? new();
        }
        else
        {
            msgAtts = thread.SnapshotMessageAttachments(fromHistory: false);
        }

        if (!opts.UserMessagePreadded)
        {
            thread.History.Add(new Prompt
            {
                AuthorName  = opts.Username,
                Text        = prompt,
                Timestamp   = DateTime.Now,
                Attachments = msgAtts.Count > 0 ? msgAtts.ToList() : null,
                IsVisible   = !opts.ChatHidden
            });
            thread.RaiseUpdated();
        }

        return (threadAtts, msgAtts);
    }

    private (string baseSystem, string budgetsBlock, string thinkSuffix) BuildSystemBlock(
        Thread thread, int thinkBudget, int respBudget, bool Think)
    {
        // Persona comes FIRST — byte-identical across turns so the KV prefix cache survives.
        string persona   = UsePersona ? PersonaStore.Get() : "";
        string roleBody  = BuildSystemPrompt(thread);
        string roleBlock = thread.PlatformContext is null
            ? roleBody
            : $"{roleBody}\n\n{thread.PlatformContext}";
        string baseSystem = persona.Length == 0 ? roleBlock : $"# Persona\n{persona}\n\n{roleBlock}";
        baseSystem += PersistentContext(thread);
        if (thread.tools.ContainsKey("list_tools"))
            baseSystem += "\n\n" + SharedPrompts.ToolSystemBlock;

        // [Budgets] at the BOTTOM — per-turn values must not invalidate the cached prefix above.
        string budgetsBlock = BuildBudgets(thinkBudget, respBudget, BudgetContext, MaxToolCalls);
        string thinkSuffix  = Think ? "" : "\n<|think_off|>";

        return (baseSystem, budgetsBlock, thinkSuffix);
    }

    private List<object> BuildMessages(Thread thread, string prompt, PromptOptions opts,
        List<Attachment> threadAtts, List<Attachment> msgAtts,
        int maxChars, string baseSystem, string budgetsBlock, string thinkSuffix)
    {
        List<ThreadMessage> chatHistory = thread.GetChatHistory(MemoryLimit, maxChars);

        List<ThreadMessage> collapsed = new();
        foreach (ThreadMessage m in chatHistory)
        {
            if (collapsed.Count > 0 && collapsed[^1].Role == m.Role)
                collapsed[^1] = collapsed[^1] with { Content = collapsed[^1].Content + "\n" + m.Content };
            else
                collapsed.Add(m);
        }

        if (opts.AugmentedPrompt is not null && collapsed.Count > 0)
            collapsed[^1] = collapsed[^1] with { Content = opts.AugmentedPrompt };

        List<object> messages = new() { new { role = "system", content = baseSystem + budgetsBlock + thinkSuffix } };

        for (int i = 0; i < collapsed.Count - 1; i++)
        {
            ThreadMessage m = collapsed[i];
            messages.Add(new { role = m.Role, content = $"{m.Username}: {m.Content}" });
        }

        // modeNudge is a trailing system message — mid-conversation system messages ARE rendered by Qwen3's template.
        string memoryBlock = opts.RecallNotes != null
            ? $"[ARI's Memories]\n{(string.IsNullOrWhiteSpace(opts.RecallNotes) ? "none" : opts.RecallNotes.Trim())}\n\n"
            : string.Empty;

        if (collapsed.Count > 0)
        {
            ThreadMessage current   = collapsed[^1];
            string        promptText = $"{memoryBlock}{current.Username}: {current.Content}";

            var threadImages = new List<Attachment>();
            var threadTexts  = new List<Attachment>();
            foreach (Attachment a in threadAtts)
            {
                if (a.IsImage) threadImages.Add(a);
                else           threadTexts.Add(a);
            }
            var msgImages = new List<Attachment>();
            var msgTexts  = new List<Attachment>();
            foreach (Attachment a in msgAtts)
            {
                if (a.IsImage) msgImages.Add(a);
                else           msgTexts.Add(a);
            }

            bool hasThreadContent = threadImages.Count > 0 || threadTexts.Count > 0;
            bool hasMsgContent    = msgImages.Count > 0    || msgTexts.Count > 0;

            if (!hasThreadContent && !hasMsgContent)
            {
                messages.Add(new { role = "user", content = promptText });
                if (opts.ModeNudge is not null) messages.Add(new { role = "system", content = opts.ModeNudge });
            }
            else
            {
                List<object> contentParts = new();
                bool hasTools = thread.tools.Count > 0;

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
                if (opts.ModeNudge is not null) messages.Add(new { role = "system", content = opts.ModeNudge });
            }
        }

        return messages;
    }

    private Dictionary<string, object?> BuildRequest(Thread thread, List<object> messages,
        int maxTokens, int thinkBudget, int thinkingBudgetOverride, object[]? toolSchemas, bool Think)
    {
        if (Server is null)
            throw new InvalidOperationException($"[{Name}] has no Server — cannot resolve sampler settings.");
        Server srv = Server;
        SamplerSettings s = ResolveSampler(thread);

        Dictionary<string, object?> body = new()
        {
            ["model"]          = "local",
            ["messages"]       = messages,
            ["stream"]         = true,
            ["stream_options"] = new { include_usage = true },
            ["max_tokens"]     = maxTokens,
            ["temperature"]           = s.Temperature         ?? SamplerSettings?.Temperature         ?? srv.Temperature,
            ["top_p"]                 = s.TopP                ?? SamplerSettings?.TopP                ?? srv.TopP,
            ["top_k"]                 = s.TopK                ?? SamplerSettings?.TopK                ?? srv.TopK,
            ["min_p"]                 = s.MinP                ?? SamplerSettings?.MinP                ?? srv.MinP,
            ["repeat_penalty"]        = s.RepeatPenalty       ?? SamplerSettings?.RepeatPenalty       ?? srv.RepeatPenalty,
            ["presence_penalty"]      = s.PresencePenalty     ?? SamplerSettings?.PresencePenalty     ?? srv.PresencePenalty,
            ["frequency_penalty"]     = s.FrequencyPenalty    ?? SamplerSettings?.FrequencyPenalty    ?? srv.FrequencyPenalty,
            ["top_n_sigma"]           = SamplerSettings?.TopNSigma           ?? srv.TopNSigma,
            ["typical_p"]             = SamplerSettings?.TypicalP             ?? srv.TypicalP,
            ["xtc_probability"]       = SamplerSettings?.XtcProbability       ?? srv.XtcProbability,
            ["xtc_threshold"]         = SamplerSettings?.XtcThreshold         ?? srv.XtcThreshold,
            ["dynatemp_range"]        = SamplerSettings?.DynatempRange        ?? srv.DynatempRange,
            ["dynatemp_exponent"]     = SamplerSettings?.DynatempExp          ?? srv.DynatempExp,
            ["repeat_last_n"]         = SamplerSettings?.RepeatLastN          ?? srv.RepeatLastN,
            ["dry_multiplier"]        = SamplerSettings?.DryMultiplier        ?? srv.DryMultiplier,
            ["dry_base"]              = SamplerSettings?.DryBase              ?? srv.DryBase,
            ["dry_allowed_length"]    = SamplerSettings?.DryAllowedLength     ?? srv.DryAllowedLength,
            ["dry_penalty_last_n"]    = SamplerSettings?.DryPenaltyLastN      ?? srv.DryPenaltyLastN,
            ["dry_sequence_breakers"] = SamplerSettings?.DrySequenceBreakers  ?? srv.DrySequenceBreakers,
            ["mirostat"]              = SamplerSettings?.Mirostat             ?? srv.Mirostat,
            ["mirostat_tau"]          = SamplerSettings?.MirostatEnt          ?? srv.MirostatEnt,
            ["mirostat_eta"]          = SamplerSettings?.MirostatLr           ?? srv.MirostatLr,
            ["seed"]                  = SamplerSettings?.Seed                 ?? srv.Seed,
        };

        // Thinking is fixed per turn — flipping enable_thinking changes the chat template and busts the KV cache.
        if (!Think)
        {
            body["thinking"]             = false;
            body["enable_thinking"]      = false;
            body["chat_template_kwargs"] = new { enable_thinking = false };
        }
        else if (BudgetThinking > 0 || thinkingBudgetOverride > 0)
        {
            int budget = thinkingBudgetOverride > 0 ? thinkingBudgetOverride : BudgetThinking;
            // thinking_budget_tokens is the field llama.cpp actually reads; thinking_budget is silently ignored.
            // Requires server started WITHOUT --reasoning-budget so per-request overrides stay active.
            body["thinking_budget_tokens"] = budget;
            body["enable_thinking"]        = true;
            body["chat_template_kwargs"]   = new { enable_thinking = true };
        }
        else
        {
            body["thinking"]             = true;
            body["enable_thinking"]      = true;
            body["chat_template_kwargs"] = new { enable_thinking = true };
        }

        if (toolSchemas is not null) body["tools"] = toolSchemas;
        if (Slot is not null)
        {
            int slotIndex = srv.Slots.FindIndex(sl => sl.Id == Slot.Id);
            if (slotIndex >= 0) body["id_slot"] = slotIndex;
        }

        return body;
    }

    private string? NeedsNudge(Thread thread, string stepText, StringBuilder reasoningBuilder, int reasoningStartLen)
    {
        string tail = stepText.TrimEnd();
        bool promisesAction = tail.Length > 0 && (
            tail.EndsWith(":")
            || System.Text.RegularExpressions.Regex.IsMatch(tail,
                @"(?i)\b(let me|let's|i'll|i will|i'm going to|i need to|now i'll|first,? i|next,? i)\b[^.!?]{0,100}$"));
        bool mentionsVerb = System.Text.RegularExpressions.Regex.IsMatch(tail,
            @"(?i)\b(read|check|run|build|test|look|examine|open|search|edit|create|add|update|fix|verify|inspect|modify|write|review|rebuild|re-?run)\b");
        bool placeholderTurn = tail.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(
            tail, @"^\$?\{[\w .-]{1,60}\}$");
        bool emptyTurn = tail.Length == 0 || placeholderTurn;
        // Tool calls inside <think> land in reasoning_content and are never executed.
        bool callsInThinking = emptyTurn && reasoningBuilder.Length > reasoningStartLen &&
            reasoningBuilder.ToString(reasoningStartLen, reasoningBuilder.Length - reasoningStartLen)
                .Contains("<tool_call>", StringComparison.OrdinalIgnoreCase);

        if (!((promisesAction && mentionsVerb) || emptyTurn)) return null;

        string why = callsInThinking
            ? "You wrote your tool calls INSIDE your thinking block — tool calls made while thinking are NOT executed. Nothing ran"
            : placeholderTurn
            ? $"Your entire answer was the literal placeholder \"{tail}\" — that is not content; write the actual text it stands for"
            : emptyTurn
            ? "Your reasoning finished but you produced no answer and no tool call — the turn was empty"
            : "You described an action but didn't perform it — no tool call was made";

        Shared.Logger.LogInformation("[{Agent}] ({Thread}) premature-stop nudge ({Kind}).", Name, thread.Key,
            callsInThinking ? "tool-calls-inside-thinking" : placeholderTurn ? "placeholder-answer" : emptyTurn ? "empty-turn-after-reasoning" : "narrated-no-action");

        return $"[System: {why}. Don't stop here: take the next concrete action now — issue the tool call AFTER your thinking ends, as your answer (and keep working until the task is done AND the project builds), or if you are genuinely finished, give the user a short summary of what you changed. Do not reply with nothing, and do not repeat a tool call you already made.]";
    }

    private static string CleanResponse(StringBuilder contentBuilder, StringBuilder responseBuilder)
    {
        string responseText = contentBuilder.Length > 0
            ? contentBuilder.ToString() + responseBuilder.ToString()
            : responseBuilder.ToString();
        responseText = System.Text.RegularExpressions.Regex.Replace(responseText, @"^\s*ARI\s*:\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Strip soft-thinking blocks emitted as literal text (Qwen3.x behaviour when think_off is not honoured).
        responseText = System.Text.RegularExpressions.Regex.Replace(
            responseText, @"<think>[\s\S]*?</think>", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return responseText
            .Replace("<think>",       "")
            .Replace("</think>",      "")
            .Replace("<|think_off|>", "")
            .Replace("<|think_on|>",  "")
            .Trim();
    }

    private void FinalizeResponse(Thread thread, string prompt, PromptOptions opts,
        string responseText, Response ariResponse, StringBuilder reasoningBuilder,
        List<string> toolResults, TurnClock clock, double elapsed,
        int completionTokens, int promptTokens, int prefilledTokens, double prefillTokPerSec,
        int maxTokens, int estimatedTextTokens, bool hadImages,
        List<TraceStep> trace, StringBuilder responseBuilder, int toolCallCount = 0)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            throw new LlmRequestFailedException("LLM response was empty.");

        string traceText = System.Text.RegularExpressions.Regex.Replace(responseBuilder.ToString(), @"<!--ari-[\s\S]*?-->", "");
        traceText = System.Text.RegularExpressions.Regex.Replace(traceText, "<div class=\"tool-use\">[\\s\\S]*?</div>", "");
        traceText = traceText.Replace("<|think_off|>", "").Replace("<|think_on|>", "").Trim();
        traceText = System.Text.RegularExpressions.Regex.Replace(traceText, @"^\s*ARI\s*:\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (traceText.Length > 0) trace.Add(new TraceStep { Kind = "text", Text = traceText.Trim() });

        double tokPerSec = completionTokens > 0 ? completionTokens / elapsed : 0;

        if (!SuppressLog())
        {
            Shared.Logger.LogInformation("[{Agent}] ({Thread}) responded in {Seconds}s (prefill {Prefill}s, thinking {Thinking}s, typing {Typing}s; {Tokens} tokens, {TokPerSec} t/s)",
                Name, thread.Key, elapsed.ToString("F1"),
                clock.Prefill.ToString("F1"), clock.Thinking.ToString("F1"), clock.Typing.ToString("F1"),
                completionTokens, tokPerSec.ToString("F1"));

            if (prefilledTokens >= 0 && promptTokens > 0)
            {
                int reusedPct = (int)((promptTokens - prefilledTokens) * 100.0 / promptTokens);
                Shared.Logger.LogInformation("[{Agent}] ({Thread}) cache-hit: prefilled {Prefilled} of {Prompt} ({Pct}% reused) @ {PrefillTokPerSec} t/s",
                    Name, thread.Key, prefilledTokens, promptTokens, reusedPct, prefillTokPerSec.ToString("F1"));
            }

            if (maxTokens > 0 && completionTokens >= maxTokens * TOKEN_WARNING_RATIO)
                Shared.Logger.LogWarning("[{Agent}] ({Thread}) token usage at {Pct}% of limit ({Used}/{Max})",
                    Name, thread.Key, (int)(completionTokens * 100.0 / maxTokens), completionTokens, maxTokens);

            Shared.Logger.LogInformation("[{Agent}] ({Thread}) response\n\"{Response}\"",
                Name, thread.Key, ExtractLogText(responseText));
        }

        List<string> noteParts = new();
        if (!string.IsNullOrEmpty(opts.RecallNotes)) noteParts.Add(opts.RecallNotes.Trim());
        if (toolResults.Count > 0)                   noteParts.Add(string.Join("\n\n", toolResults).TrimEnd());
        string? combinedNotes = noteParts.Count > 0 ? string.Join("\n\n", noteParts) : null;

        thread.liveCallInfo = null;

        ariResponse.Content                        = ContentBlock.Parse(responseText);
        ariResponse.Data.DebugResponseText         = responseText;
        ariResponse.Reasoning                      = reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null;
        ariResponse.ThinkingSeconds                = clock.Thinking;
        ariResponse.PrefillSeconds                 = clock.Prefill;
        ariResponse.TypingSeconds                  = clock.Typing;
        ariResponse.TotalSeconds                   = elapsed;
        ariResponse.RecallSeconds                  = opts.RecallSeconds;
        ariResponse.ToolCallCount                  = toolCallCount > 0 ? toolCallCount : null;
        ariResponse.RecallNotes                    = combinedNotes;
        ariResponse.ContextSummary                 = opts.ContextSummary;
        ariResponse.Data.CompletionTokens          = completionTokens;
        ariResponse.Data.OutputTokenLimit          = maxTokens > 0 ? maxTokens : 0;
        ariResponse.Data.PromptTokens              = promptTokens;
        ariResponse.Data.PrefilledPromptTokens     = prefilledTokens;
        ariResponse.Data.ContextTokenLimit         = BudgetContext;
        ariResponse.Data.HadImageAttachments       = hadImages;
        ariResponse.Data.EstimatedTextPromptTokens = estimatedTextTokens;
        ariResponse.Data.ImageTokenLimit           = 0;
        ariResponse.Data.PrefillTokPerSec          = prefillTokPerSec;
        ariResponse.State                          = State.Complete;
        ariResponse.StreamText                     = null;
        thread.streamingResponse                   = null;
        thread.RaiseStreamingFinished();

        thread.OnResponseComplete();
        thread.RaiseExchangeCompleted(prompt, responseText);

    }

    // ── Static helpers ────────────────────────────────────────────────────────

    private static string ExtractLogText(string content)
    {
        var sb = new StringBuilder();
        foreach (object block in ContentBlock.Parse(content))
        {
            if (block is TextBlock tb) sb.Append(tb.Text);
        }
        return sb.ToString().Replace("<!--ari-batch-end-->", "").Trim();
    }

    private static string? ContentOf(object m) => m.GetType().GetProperty("content")?.GetValue(m) as string;

    private static void Compact(List<object> messages, List<(int Index, string CallId, string Name, string? Path)> slots,
        int maxContextTokens, int highPct, int lowPct, FileSnapshots? snapshots)
    {
        if (maxContextTokens <= 0) return;

        long trigger = (long)(maxContextTokens * (long)CHARS_PER_TOKEN * (highPct / 100.0));
        long target  = (long)(maxContextTokens * (long)CHARS_PER_TOKEN * (lowPct  / 100.0));
        long total   = 0;
        foreach (object m in messages) total += ContentOf(m)?.Length ?? 0;

        // Stub down to the LOW watermark, not just below trigger — prevents re-stubbing (and re-prefill) every turn.
        if (total <= trigger) return;

        int stubbable = slots.Count - COMPACT_KEEP_RECENT;
        for (int i = 0; i < stubbable && total > target; i++)
        {
            (int idx, string callId, string name, string? path) = slots[i];
            if (idx < 0 || idx >= messages.Count) continue;
            string? cur = ContentOf(messages[idx]);
            if (cur is null || cur.Length < 200) continue;
            string stub = path is not null
                ? $"[Earlier {name} of '{path}' omitted to save context — re-read it if you still need it; that is not blocked, the prior read no longer counts.]"
                : $"[Earlier {name} output omitted to save context — re-run the tool if you need it again.]";
            messages[idx] = new { role = "tool", tool_call_id = callId, name, content = stub };
            total -= cur.Length - stub.Length;
            // Invalidate the dedup ledger so the model can re-read the file the guard blocked it from re-reading.
            if (path is not null) snapshots?.InvalidateReads(path);
        }
    }

    private static void ReplaceInBuilder(StringBuilder sb, string oldText, string newText)
    {
        string s = sb.ToString();
        int pos = s.LastIndexOf(oldText, StringComparison.Ordinal);
        if (pos < 0) return;
        sb.Remove(pos, oldText.Length).Insert(pos, newText);
    }
}

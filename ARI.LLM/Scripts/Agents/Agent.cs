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
    [JsonPropertyName("name")]          public string  Name          { get; init; } = "";
    [JsonPropertyName("serverName")]    public string  ServerName    { get; set; }  = "";
    [JsonPropertyName("systemPrompt")]  public string  SystemPrompt  { get; init; } = "";
    [JsonPropertyName("enabled")]       public bool    Enabled       { get; init; }
    [JsonPropertyName("maxTokens")]     public int     MaxTokens     { get; init; } = -1;
    [JsonPropertyName("maxToolCalls")]  public int     MaxToolCalls  { get; init; }
    [JsonPropertyName("think")]         public bool    Think         { get; init; }
    [JsonPropertyName("thinkingBudget")]public int     ThinkingBudget{ get; init; }
    // Hard per-turn reasoning cap (chars), enforced at the stream level since this model ignores the
    // soft thinking_budget. Once cumulative reasoning in a turn crosses this, thinking is force-disabled
    // for the rest of the turn so the model must commit to output. 0 = no cap. Only the planner uses it.
    [JsonPropertyName("reasoningCharCap")] public int  ReasoningCharCap { get; init; }
    [JsonPropertyName("slot")]          public int?    Slot          { get; set; }
    [JsonPropertyName("temperature")]   public double? Temperature   { get; init; }
    [JsonPropertyName("topP")]          public double? TopP          { get; init; }
    [JsonPropertyName("topK")]          public int?    TopK          { get; init; }
    [JsonPropertyName("repeatPenalty")] public double? RepeatPenalty { get; init; }
    [JsonPropertyName("presencePenalty")]  public double? PresencePenalty  { get; init; }
    [JsonPropertyName("frequencyPenalty")]  public double? FrequencyPenalty  { get; init; }
    [JsonPropertyName("maxContextTokens")] public int     MaxContextTokens  { get; init; }
    // When true, send tools via the native OpenAI `tools` field and parse native tool_calls, instead
    // of the text protocol (BuildToolCatalog + ParseTextCalls). Native relies on llama.cpp's --jinja
    // chat-template tool parsing (qwen3_coder format) being reliable for this model/build.
    [JsonPropertyName("nativeTools")]   public bool    NativeTools   { get; init; }

    // ── Runtime-only ─────────────────────────────────────────────────────────
    [JsonIgnore] public string Endpoint { get; internal set; } = "";

    // 0 = unlimited. Overridden by agents that trim short-term history.
    [JsonIgnore] internal virtual int  MemoryLimit => 0;

    [JsonIgnore] internal virtual bool QuietLogging      => false;
    [JsonIgnore] internal virtual bool SuppressPromptLog => false;

    // ── Sampling defaults (overridden by agent config; server defaults are the baseline) ──
    private const int    CHARS_PER_TOKEN     = 4;
    private const double TEMPERATURE         = 0.7;
    private const double TOP_P               = 0.95;
    private const int    TOP_K               = 20;
    private const double MIN_P               = 0.05;
    private const double REPEAT_PENALTY      = 1.0;
    private const double TOKEN_WARNING_RATIO = 0.8;
    private const double COMPACT_RATIO_HIGH  = 0.6;  // trigger: compact once context chars exceed this fraction of budget
    private const double COMPACT_RATIO_LOW   = 0.4;  // target: stub down to here in one pass (hysteresis — one prefix invalidation buys many stable turns)
    private const int    COMPACT_KEEP_RECENT = 3;
    private const int    MAX_DEGRADE_EVENTS  = 5;
    private const int    DEFAULT_MEMORY_LIMIT = 25;
    private const string ATTACHMENT_DIVIDER  = "-------------------";

    private readonly HttpClient httpClient = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    // ── Context-building hooks (overridden by specialised agents) ────────────
    internal virtual string BuildPersistentContext(Thread thread)    => "";
    internal virtual string RenderDynamicContextBlock(Thread thread) => "";

    // ── Tool-loop hooks (Code overrides; base = generic, no-guard behaviour) ──────
    // Per-turn state for the tool loop. The base carries only what the generic loop reads; Code's
    // CodeTurnState subclass adds its guard counters. Created fresh per SendPrompt turn (never shared
    // between threads) so one agent instance can serve many threads concurrently.
    protected class ToolTurnState
    {
        public bool ForceNoMoreTools;   // a guard cut tools off for the rest of this turn
        public bool BlindFirstRead;     // transient: the last read was of a file not previewed this turn
    }
    protected virtual ToolTurnState CreateToolTurnState() => new();
    // Called once per tool batch, before its calls are executed.
    protected virtual void OnToolBatchStart(ToolTurnState state) { }
    // Mid-stream veto of an edit_file whose target hasn't been read this turn. Returns an abort message or null.
    protected virtual string? StreamEditPrecheck(Thread thread, ToolTurnState state, string toolName, string argsJson,
        IEnumerable<string> pendingReadPaths, List<Attachment> threadAtts, List<Attachment> msgAtts) => null;
    // Before executing a tool: return a short-circuit result (dedup / nudge / cache hit) or null to proceed.
    protected virtual string? PreToolGuard(Thread thread, ToolTurnState state, string toolName, string callId, string argsJson) => null;
    // After executing a tool: post-process / track state; returns the (possibly modified) result. May throw to abort the turn.
    protected virtual string PostToolProcess(Thread thread, ToolTurnState state, string toolName, string argsJson, string result) => result;
    // After a tool result is appended to messages: maintain read-stub / cache bookkeeping.
    protected virtual void AfterToolAppended(ToolTurnState state, List<object> messages, string toolName, string callId, string argsJson, string result, int addedIndex) { }
    // At batch end: should the turn stop for lack of progress?
    protected virtual bool OnBatchEndShouldBreak(Thread thread, ToolTurnState state, bool productiveBatch) => false;

    internal List<ThreadMessage> ContextSnapshot(Thread thread)
    {
        int maxChars = MaxContextTokens > 0 ? MaxContextTokens * 2 : 0;
        return thread.GetChatHistory(MemoryLimit, maxChars);
    }

    public (int Used, int Limit) GetContextStats(Thread? thread)
    {
        if (thread is null) return (0, MaxContextTokens);
        List<ThreadMessage> ctx = ContextSnapshot(thread);
        int chars = ctx.Sum(m => (m.Username?.Length ?? 0) + 2 + (m.Content?.Length ?? 0));
        return (chars / CHARS_PER_TOKEN, MaxContextTokens);
    }

    // ── Send loop ────────────────────────────────────────────────────────────

    internal async Task<string> SendPrompt(
        Thread              thread,
        string              prompt,
        string              username               = "user",
        string?             augmentedPrompt        = null,
        string?             recallNotes            = null,
        string?             contextSummary         = null,
        int                 maxTokensOverride      = 0,
        CancellationToken   ct                     = default,
        bool                userMessagePreadded    = false,
        Func<string, Task>? onDelta                = null,
        int                 thinkingBudgetOverride = 0,
        bool                chatHidden             = false,
        int?                thinkSeconds           = null)
    {
        await thread.sendLock.WaitAsync(ct);
        try
        {
            return await Send(thread, prompt, username, augmentedPrompt, recallNotes, contextSummary, maxTokensOverride, ct, userMessagePreadded, onDelta, thinkingBudgetOverride, chatHidden, thinkSeconds);
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
        }
    }

    /// <summary>True if the reasoning buffer ends on a sentence boundary (. ! ? or newline), ignoring trailing
    /// whitespace — the point at which it is safe to cut thinking off gracefully (see the #35 time budget).</summary>
    private static bool EndsSentence(System.Text.StringBuilder sb)
    {
        int i = sb.Length - 1;
        while (i >= 0 && (sb[i] == ' ' || sb[i] == '\t' || sb[i] == '\r')) i--;
        if (i < 0) return false;
        char c = sb[i];
        return c is '.' or '!' or '?' or '\n';
    }

    // Mid-thought budget nudges, injected INSIDE <think> (hidden from the user; visible only in the debug
    // trace). Escalation ladder on the THINKING clock: 80% warn → 100% finish-the-sentence → 110% guide the
    // thought shut (assistant-voice prime that leads straight to </think>) → 120% force-close </think>.
    private const string Nudge80  = "\n[System: You've used 80% of your thinking budget — start wrapping up your chain of thought.]\n";
    private const string Nudge100 = "\n[System: Your thinking budget is spent. Finish your current sentence, then close your thinking and act.]\n";
    private const string Nudge110 = "\n\nOkay — I have thought about this long enough. I'll stop here and act on what I have: ";

    /// <summary>Wall-clock split of one turn (#35 v2). Prefill = request sent → first delta of each request
    /// (the server reading the prompt). Thinking/Typing tick ONLY while deltas are actually arriving — an
    /// inter-delta gap over 2s is a server stall or tool wait and counts toward no bucket. The thinking
    /// budget compares against Thinking, never total elapsed, so a slow prefill can no longer eat the
    /// model's thinking allowance (a 223s prefill once consumed a 30s budget before the model said a word).</summary>
    private sealed class TurnClock
    {
        public double Prefill, Thinking, Typing;
        private DateTime  sent;
        private DateTime? lastDelta;

        public void RequestSent() { sent = DateTime.UtcNow; lastDelta = null; }

        public void Tick(bool reasoning)
        {
            DateTime now = DateTime.UtcNow;
            if (lastDelta is null)
                Prefill += (now - sent).TotalSeconds;
            else
            {
                double gap = (now - lastDelta.Value).TotalSeconds;
                if (gap <= 2) { if (reasoning) Thinking += gap; else Typing += gap; }
            }
            lastDelta = now;
        }
    }

    // Grade-10 (unlimited budget) periodic time-awareness nudge — injected every 30s of reasoning.
    private static string PeriodicNudge(int seconds) =>
        $"\n[System: You have been thinking for {seconds} seconds. The user is waiting — long silences are a poor experience. Reach your conclusion as soon as you reasonably can.]\n";

    /// <summary>POST the turn's messages to llama.cpp /apply-template → the formatted prompt (ends at the assistant
    /// generation point, i.e. "…assistant\n&lt;think&gt;\n"), which we extend to continue a thought mid-stream.</summary>
    private async Task<string> ApplyTemplate(IEnumerable<object> messages, HttpClient http, CancellationToken ct)
    {
        HttpRequestMessage req = new(HttpMethod.Post, $"{Endpoint}/apply-template")
            { Content = new StringContent(JsonSerializer.Serialize(new { messages }), Encoding.UTF8, "application/json") };
        using HttpResponseMessage resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("prompt", out JsonElement p) ? p.GetString() ?? "" : "";
    }

    /// <summary>
    /// Mid-thought budget warnings. The chat API can't continue a thought, so we drop to raw /completion: re-feed
    /// the reasoning so far + a nudge INSIDE &lt;think&gt;, let the model keep thinking, then stream the post-
    /// &lt;/think&gt; answer. Cache-friendly by construction: the continuation prompt is the SAME rendered
    /// conversation plus tokens the server itself just generated, so the KV prefix survives — this NEVER flips
    /// enable_thinking (a template flip invalidates the whole cached prompt; a 16k context re-prefilled twice
    /// that way). Escalation: 80% cue → 100% wrap-up cue → forced &lt;/think&gt; only past 150% of the budget,
    /// all measured on the THINKING clock (time actually receiving reasoning), never wall-clock.
    /// Reasoning stays hidden (appended to reasoningBuilder for the trace only); only the answer reaches onDelta,
    /// so it's invisible in the chat UI. Returns when the answer is complete; contentBuilder holds it for the
    /// caller's normal (text-mode) post-stream parsing.
    /// </summary>
    private async Task ContinueThinking(
        IEnumerable<object> messages, IReadOnlyDictionary<string, object> body, HttpClient http, Thread thread,
        StringBuilder reasoningBuilder, StringBuilder responseBuilder, StringBuilder contentBuilder,
        string firstNudge, TurnClock clock, int thinkDeadlineSec, bool warned80Already, bool warned100Already,
        int lastPeriodicSec, Func<string, Task>? onDelta, CancellationToken ct)
    {
        string basePrompt    = await ApplyTemplate(messages, http, ct);
        string suffix        = firstNudge;
        bool   warned80      = warned80Already;
        bool   warned100     = warned100Already;
        bool   guided110     = false;    // 110% assistant-voice close prime sent?
        bool   startInContent = false;   // true when the suffix already closed </think>

        while (true)
        {
            string contPrompt = basePrompt + reasoningBuilder.ToString() + suffix;

            var cbody = new Dictionary<string, object>
            {
                ["prompt"]       = contPrompt,
                ["stream"]       = true,
                ["cache_prompt"] = true,
                ["n_predict"]    = body.TryGetValue("max_tokens", out object? mt) ? mt : 4096,
            };
            foreach (string k in new[] { "temperature", "top_p", "top_k", "min_p", "repeat_penalty", "presence_penalty", "frequency_penalty" })
                if (body.TryGetValue(k, out object? v)) cbody[k] = v;
            if (Slot.HasValue) cbody["id_slot"] = Slot.Value;

            HttpRequestMessage req = new(HttpMethod.Post, $"{Endpoint}/completion")
                { Content = new StringContent(JsonSerializer.Serialize(cbody), Encoding.UTF8, "application/json") };
            clock.RequestSent();
            using HttpResponseMessage resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            using Stream stream = await resp.Content.ReadAsStreamAsync(ct);
            using StreamReader reader = new(stream);

            bool    inContent  = startInContent;
            string  carry      = "";          // hold-back to catch </think> spanning chunks
            string? nextSuffix = null;         // set when a threshold fires mid-round → loop with this suffix
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                string payload = line["data: ".Length..];
                if (payload == "[DONE]") break;
                string piece; bool stop;
                try
                {
                    using JsonDocument d = JsonDocument.Parse(payload);
                    piece = d.RootElement.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
                    stop  = d.RootElement.TryGetProperty("stop", out JsonElement st) && st.ValueKind == JsonValueKind.True;
                }
                catch { continue; }

                if (piece.Length > 0)
                {
                    clock.Tick(reasoning: !inContent);
                    if (!inContent)
                    {
                        string buf = carry + piece;
                        int idx = buf.IndexOf("</think>", StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            reasoningBuilder.Append(buf[..idx]);
                            inContent = true; carry = "";
                            string after = buf[(idx + "</think>".Length)..];
                            if (after.Length > 0) { responseBuilder.Append(after); if (onDelta is not null) await onDelta(contentBuilder.ToString() + responseBuilder.ToString()); }
                        }
                        else
                        {
                            int safe = Math.Max(0, buf.Length - 7);   // hold back a possible partial "</think>"
                            reasoningBuilder.Append(buf[..safe]);
                            carry = buf[safe..];
                        }
                    }
                    else
                    {
                        responseBuilder.Append(piece);
                        if (onDelta is not null) await onDelta(contentBuilder.ToString() + responseBuilder.ToString());
                    }
                }

                // Threshold checks while still thinking — all on the THINKING clock (see TurnClock).
                if (!inContent && thinkDeadlineSec < 0)
                {
                    // Grade 10 (unlimited): no deadline, just a time-awareness nudge every 30s of thinking.
                    int el = (int)clock.Thinking;
                    if (el - lastPeriodicSec >= 30)
                    {
                        lastPeriodicSec = el;
                        reasoningBuilder.Append(carry); carry = "";
                        nextSuffix = PeriodicNudge(el);
                        break;
                    }
                }
                else if (!inContent && thinkDeadlineSec > 0)
                {
                    double frac = clock.Thinking / thinkDeadlineSec;
                    string? escalation =
                        frac >= 1.20              ? "</think>\n\n"   // runaway stop — close the thought ourselves; a prompt-level </think> keeps the template (and KV cache) intact
                      : frac >= 1.10 && !guided110 ? Nudge110         // guide it shut: assistant-voice prime that leads straight to </think>
                      : frac >= 1.00 && !warned100 ? Nudge100         // budget spent: finish the sentence, then close
                      : frac >= 0.80 && !warned80 && thinkDeadlineSec >= 30 ? Nudge80   // warning — only on budgets big enough for it not to be spam
                      : null;
                    if (escalation is not null)
                    {
                        if (escalation == Nudge110) guided110 = true;
                        if (escalation == Nudge100) warned100 = true;
                        if (escalation == Nudge80)  warned80  = true;
                        reasoningBuilder.Append(carry); carry = "";
                        nextSuffix = escalation;
                        break;
                    }
                }

                if (stop) { reasoningBuilder.Append(carry); carry = ""; break; }
            }

            if (nextSuffix is null) return;   // round finished naturally — the answer is complete
            suffix         = nextSuffix;
            startInContent = nextSuffix.StartsWith("</think>", StringComparison.Ordinal);
            Shared.Logger.LogInformation("[{Agent}] ({Thread}) thinking continuation: {Why} ({Thinking}s thinking / {Budget}s budget).",
                Name, thread.Key,
                startInContent ? "120% → forced </think>"
                : guided110    ? "110% → guided close"
                : warned100    ? "100% → finish-sentence cue"
                :                "80% warning",
                clock.Thinking.ToString("F0"), thinkDeadlineSec);
        }
    }

    private async Task<string> Send(
        Thread              thread,
        string              prompt,
        string              username,
        string?             augmentedPrompt,
        string?             recallNotes,
        string?             contextSummary,
        int                 maxTokensOverride,
        CancellationToken   ct,
        bool                userMessagePreadded,
        Func<string, Task>? onDelta               = null,
        int                 thinkingBudgetOverride = 0,
        bool                chatHidden             = false,
        int?                thinkSeconds           = null)
    {
        // Thinking on/off is a PIPELINE-LEVEL setting (the agent's configured Think) — never flipped per
        // turn or mid-turn. Flipping it changes the chat template, which invalidates the server's entire
        // cached prompt prefix and forces a full re-prefill (~minutes at local prefill speeds). Per-turn
        // thinkSeconds (from the Appraisal grade) only sizes the BUDGET; grade 0 maps to a tiny budget
        // (3s → the 100% finish-sentence cue fires almost immediately), never to think-off.
        bool effectiveThink   = Think;
        int  thinkDeadlineSec = thinkSeconds.GetValueOrDefault(0);   // >0 = thinking-clock budget (#35); <=0 = none
        thread.LastMessageAt = DateTime.UtcNow;

        thread.inactivityTimer?.Dispose();
        thread.inactivityTimer = null;

        thread.dormantTimer?.Dispose();
        thread.dormantTimer = null;
        thread.State = ThreadState.Streaming;

        if (thread.ariRepliedAt != DateTime.MinValue)
        {
            int sampleWindow = MemoryLimit > 0 ? MemoryLimit : DEFAULT_MEMORY_LIMIT;
            thread.responseSamples.Add(DateTime.UtcNow - thread.ariRepliedAt);
            if (thread.responseSamples.Count > sampleWindow)
                thread.responseSamples.RemoveAt(0);
            thread.ariRepliedAt = DateTime.MinValue;
        }

        List<Attachment> threadAtts;
        List<Attachment> msgAtts;
        lock (thread.SnapshotThreadAttachments()) { threadAtts = thread.SnapshotThreadAttachments(); }
        if (userMessagePreadded)
        {
            Prompt? lastMsg = thread.History.OfType<Prompt>().LastOrDefault();
            msgAtts = lastMsg?.Attachments?.ToList() ?? new();
        }
        else
        {
            msgAtts = thread.SnapshotMessageAttachments(fromHistory: false);
        }

        if (!userMessagePreadded)
        {
            thread.History.Add(new Prompt
            {
                AuthorName  = username,
                Text        = prompt,
                Timestamp   = DateTime.Now,
                Attachments = msgAtts.Count > 0 ? msgAtts.ToList() : null,
                IsVisible   = !chatHidden
            });
            thread.RaiseUpdated();
        }

        int maxChars = MaxContextTokens > 0 ? MaxContextTokens * 2 : 0;
        List<ThreadMessage> chatHistory = thread.GetChatHistory(MemoryLimit, maxChars);

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

        string baseSystem = thread.PlatformContext is null
            ? SystemPrompt
            : $"{SystemPrompt}\n\n{thread.PlatformContext}";
        baseSystem += BuildPersistentContext(thread);
        string thinkSuffix = effectiveThink ? "" : "\n<|think_off|>";

        List<object> messages = new List<object> { new { role = "system", content = baseSystem + thinkSuffix } };

        for (int i = 0; i < collapsed.Count - 1; i++)
        {
            ThreadMessage m = collapsed[i];
            messages.Add(new { role = m.Role, content = $"{m.Username}: {m.Content}" });
        }

        // The chat template only renders the FIRST system message — a mid-conversation system role
        // is silently dropped at templating (verified against llama-server /apply-template). Memories
        // are therefore folded into the current user message, never sent as their own system message.
        string memoryBlock = recallNotes != null
            ? $"[ARI's Memories]\n{(string.IsNullOrWhiteSpace(recallNotes) ? "none" : recallNotes.Trim())}\n\n"
            : string.Empty;

        if (collapsed.Count > 0)
        {
            ThreadMessage current   = collapsed[^1];
            string        promptText = $"{memoryBlock}{current.Username}: {current.Content}";

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
            }
        }

        if (!QuietLogging && !SuppressPromptLog)
            Shared.Logger.LogInformation("[{Agent}] ({Thread}) prompt\n\"{Prompt}\"", Name, thread.Key, prompt);

        int             maxTokens            = maxTokensOverride != 0 ? maxTokensOverride : MaxTokens;
        int             toolCallCount        = 0;
        int             parseFailures        = 0;
        int             consecutiveFallbacks = 0;
        List<string>    toolResults          = new();
        int                     continueNudges = 0;
        // All Code-specific per-turn guard state (read/edit/command dedup, build state, loop counters) lives
        // here; the generic loop only reads ToolTurnState.ForceNoMoreTools. See Code.ToolLoop.cs.
        ToolTurnState           toolTurn      = CreateToolTurnState();
        List<(int Index, string CallId, string Name)> toolResultSlots = new();
        int degradeEvents = 0;
        void Degrade()
        {
            if (++degradeEvents >= MAX_DEGRADE_EVENTS)
                throw new LlmRequestFailedException(
                    $"Tool-call formatting failed {degradeEvents} times this turn — stopping to avoid a spiral. Any changes already applied are kept.");
        }
        StringBuilder   responseBuilder  = new();
        StringBuilder   contentBuilder   = new();
        Stopwatch       sw               = Stopwatch.StartNew();
        bool            wasThinking      = false;
        int             reasoningChars   = 0;
        // Full reasoning/chain-of-thought text for this turn (debug viewer only — never re-sent to the LLM).
        StringBuilder   reasoningBuilder = new();
        TurnClock       clock            = new();  // prefill/thinking/typing wall-clock split for this turn
        bool            warned80 = false, warned100 = false;   // 80/100% thinking-budget warnings fired?
        string?         pendingNudge     = null;   // set when a warning fires → switch to the /completion continuation
        bool            warningsEnabled  = Environment.GetEnvironmentVariable("ARI_NO_WARNINGS") != "1";   // A/B toggle
        int             lastPeriodicSec  = 0;   // grade-10 unlimited: thinking-secs of the last 30s time-awareness nudge
        int             completionTokens = 0;
        int             promptTokens     = 0;
        int             prefilledTokens  = -1;   // timings.prompt_n: tokens actually re-read (rest served from KV cache)
        double          prefillTokPerSec = 0;    // timings.prompt_per_second
        bool            hadImages        = msgAtts.Any(a => a.IsImage) || threadAtts.Any(a => a.IsImage);

        int estimatedTextTokens = messages.Sum(m =>
        {
            string? content = m.GetType().GetProperty("content")?.GetValue(m) as string;
            return (content?.Length ?? 0) / CHARS_PER_TOKEN;
        });

        if (thread.liveCallInfo is { } existing)
        {
            existing.EstimatedInputTokens = estimatedTextTokens;
            existing.OutputTokenLimit     = maxTokens;
            existing.HadImages            = hadImages;
        }
        else
        {
            thread.liveCallInfo = new LiveCallInfo(Name, thread.Key, estimatedTextTokens, maxTokens, MaxContextTokens, hadImages: hadImages);
        }

        Response ariResponse = new() { Timestamp = DateTime.Now, IsVisible = !chatHidden };
        // Live trace for the deep-inspection panel — assigned now so the debug viewer sees it grow mid-stream.
        List<TraceStep> trace = new() { new TraceStep { Kind = "prompt", Text = prompt } };
        ariResponse.Trace = trace;
        thread.History.Add(ariResponse);
        thread.RaiseUpdated();
        thread.streamingResponse = ariResponse;
        thread.streamedText      = "";
        Func<string, Task>? userDelta = onDelta;
        onDelta = async text => {
            ariResponse.StreamText = text;
            // ChatHidden turns are internal orchestration (the architect's task-approvals): they must NOT push
            // live text to the chat — that broadcast (LLMModule wires thread.Streaming → the "streaming" SSE)
            // is what made the hidden approval turn overwrite the user's view. The debug view re-polls /debug
            // and still sees it; the response itself still builds via ariResponse.StreamText above.
            // Every delta is its own frame — NO throttling. A server-side timer dropped frames with no
            // trailing flush, so clients jumped from a word to whole paragraphs and card states appeared
            // only in their final form. Per-token frames are cheap locally; clients coalesce as needed.
            if (!chatHidden)
            {
                thread.streamedText = text;
                thread.RaiseStreaming(text);
            }
            if (userDelta is not null) await userDelta(text);
        };

        while (true)
        {
            bool      toolsExhausted = toolTurn.ForceNoMoreTools || (MaxToolCalls > 0 && toolCallCount >= MaxToolCalls);
            object[]? toolSchemas    = !toolsExhausted && thread.tools.Count > 0
                                        ? thread.tools.Values.Select(t => t.Schema).ToArray()
                                        : null;
            // Tool protocol. Text (default): advertise tools as text in the system prompt and parse the
            // model's <tool_call> XML ourselves (BuildToolCatalog + ParseTextCalls) — robust against the
            // llama.cpp 9430 runaway where the native parser half-parses Qwen3.6's XML and leaks the tail
            // into the arguments. Native (NativeTools=true): send the OpenAI `tools` field and let the
            // server's --jinja qwen3_coder template parse tool_calls. The text-call parser still runs as a
            // fallback in native mode, so a leak degrades rather than corrupts.
            bool   nativeTools = toolSchemas is not null && NativeTools;
            bool   textTools   = toolSchemas is not null && !NativeTools;
            string toolCatalog = textTools ? BuildToolCatalog(toolSchemas!) : "";

            // Keep the system message (and thus the whole prompt prefix) STATIC across turns so the server's
            // KV cache is reused — the volatile per-turn checklist is injected as a transient LAST message at
            // request time instead (see below). Putting changing content here at position 0 invalidates the
            // entire cache every turn, forcing a full re-process of the context (the dominant cost on a dense
            // model: ~100 t/s prompt-eval vs ~19 t/s generation).
            messages[0] = new { role = "system", content = baseSystem + toolCatalog + thinkSuffix };

            CompactToolOutput(messages, toolResultSlots, MaxContextTokens);

            if (thread.liveCallInfo is { } lci)
            {
                long totalChars = messages.Sum(m => (long)(ContentOf(m)?.Length ?? 0));
                lci.EstimatedInputTokens = (int)(totalChars / CHARS_PER_TOKEN);
            }

            if (!QuietLogging && toolCallCount == 0)
                Shared.Logger.LogInformation("[{Agent}] ({Thread}) {Tools}",
                    Name, thread.Key,
                    toolSchemas is not null ? $"{toolSchemas.Length} tool(s) available: {string.Join(", ", thread.tools.Keys)}" : "no tools registered");

            Dictionary<string, object?> body = new()
            {
                ["model"]          = "local",
                ["messages"]       = messages,
                ["stream"]         = true,
                ["stream_options"] = new { include_usage = true },
                ["max_tokens"]     = maxTokens,
                ["temperature"]    = Temperature   ?? TEMPERATURE,
                ["top_p"]          = TopP          ?? TOP_P,
                ["top_k"]          = TopK          ?? TOP_K,
                ["min_p"]          = MIN_P,
                ["repeat_penalty"] = RepeatPenalty ?? REPEAT_PENALTY
            };

            if (PresencePenalty.HasValue)  body["presence_penalty"]  = PresencePenalty.Value;
            if (FrequencyPenalty.HasValue) body["frequency_penalty"] = FrequencyPenalty.Value;

            // Thinking mode is decided ONCE for the thread/turn and never flipped mid-turn: switching
            // enable_thinking changes the chat template, which invalidates the server's whole cached
            // prompt prefix (a 16k context re-prefilled from scratch when a budget-expiry retry flipped
            // it). Budget overruns are handled with in-band cues instead — see ContinueThinking.
            if (!effectiveThink)
            {
                body["thinking"]             = false;
                body["enable_thinking"]      = false;
                body["chat_template_kwargs"] = new { enable_thinking = false };
            }
            else if (ThinkingBudget > 0 || thinkingBudgetOverride > 0)
            {
                int budget = thinkingBudgetOverride > 0 ? thinkingBudgetOverride : ThinkingBudget;
                body["thinking_budget"]      = budget;
                body["chat_template_kwargs"] = new { enable_thinking = true, thinking_budget = budget };
            }
            else
            {
                // Think on, no budget cap → deterministic unbounded-thinking toggle. The budget cap is
                // not reliably honoured by this model, so we don't depend on it: just turn thinking on.
                body["thinking"]             = true;
                body["enable_thinking"]      = true;
                body["chat_template_kwargs"] = new { enable_thinking = true };
            }

            // Native mode: hand the server the OpenAI `tools` field so its --jinja template formats and
            // parses tool calls. Text mode deliberately omits it (tools are in the system prompt instead)
            // and sets no "</tool_call>" stop, so the model can batch several calls per turn before
            // stopping naturally (<|im_end|>) — the main lever against TTFT. Guards bound any run-on.
            if (nativeTools)             body["tools"] = toolSchemas;
            if (Slot.HasValue)           body["id_slot"] = Slot.Value;

            // Cache-friendly dynamic context: append the volatile checklist as a transient LAST message just
            // for this request, then remove it so the persistent history (and its cached prefix) stays stable.
            // Only this small block + genuinely new tokens are re-processed each turn instead of the whole context.
            string dynamicBlock   = RenderDynamicContextBlock(thread);
            bool   dynamicInjected = dynamicBlock.Length > 0;
            // role "user", not "system": the chat template silently drops every system message after
            // the first (verified via /apply-template), so a system-role block here never reaches the model.
            if (dynamicInjected) messages.Add(new { role = "user", content = dynamicBlock });

            string             json    = JsonSerializer.Serialize(body);
            if (!QuietLogging)
                Shared.Logger.LogInformation("[{Agent}] ({Thread}) → request (step {Step}): max_tokens={MT}, tools={N}, msgs={Msgs}, think={Think} (et={ET}/budget={B})",
                    Name, thread.Key, toolCallCount,
                    body.TryGetValue("max_tokens", out object? mtv) ? mtv : "?",
                    toolSchemas?.Length ?? 0, messages.Count,
                    Think, body.TryGetValue("enable_thinking", out object? etv) ? etv : "unset",
                    body.TryGetValue("thinking_budget", out object? bv) ? bv : "none");
            if (dynamicInjected) messages.RemoveAt(messages.Count - 1);   // keep persistent history clean + prefix stable
            ariResponse.Data.DebugRequestJson = json;
            HttpRequestMessage request = new(HttpMethod.Post, $"{Endpoint}/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            clock.RequestSent();
            HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                string errBody = "";
                try { errBody = await response.Content.ReadAsStringAsync(ct); } catch { /* ignore */ }

                if (response.StatusCode == System.Net.HttpStatusCode.InternalServerError
                    && errBody.Contains("Failed to parse tool call arguments", StringComparison.OrdinalIgnoreCase))
                {
                    parseFailures++;
                    Degrade();
                    if (parseFailures > 2)
                        throw new LlmRequestFailedException($"Tool call JSON parse failed {parseFailures} times in a row — aborting to prevent infinite loop.");

                    Shared.Logger.LogWarning("[{Agent}] ({Thread}) Tool call JSON parse failure — injecting recovery hint.", Name, thread.Key);
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
            Dictionary<int, string> streamingMarkers = new();
            string? finishReason = null;
            string? xmlFallbackOriginalText = null;
            responseBuilder.Clear();

            (string Id, string Name, string Args, string Error)? earlyAbort = null;
            HashSet<int> precheckedCalls = new();
            int? runawayCall = null;   // a native call whose args ran away (model looping / leaking text-format markers)
            bool contentRunaway = false; // text content degenerated into a repeated-character spiral (e.g. backslashes)
            int  reasoningStartLen = reasoningBuilder.Length; // for slicing THIS request's reasoning into the trace

            // Live trace steps (#80/#111): created the moment their content starts streaming and MUTATED as
            // deltas arrive, so the DTI timeline grows in real time and every request's thinking lands as its
            // own step at the true point in the sequence (never merged into an earlier bubble). The live text
            // step is provisional — it is removed before the post-stream parser records the real text steps.
            TraceStep? liveReasoning = null;
            TraceStep? liveText      = null;

            DateTime lastProgress = DateTime.UtcNow;
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
                        completionTokens += usage.TryGetProperty("completion_tokens", out JsonElement ctEl) ? ctEl.GetInt32() : 0;
                        promptTokens      = usage.TryGetProperty("prompt_tokens",     out JsonElement ptEl) ? ptEl.GetInt32() : 0;
                    }

                    // llama-server (OAI-compat) reports how much of the context it actually re-read this turn
                    // vs served from KV cache. prompt_n is the prefilled count; the rest is a cache hit.
                    if (chunk.RootElement.TryGetProperty("timings", out JsonElement timings))
                    {
                        if (timings.TryGetProperty("prompt_n",          out JsonElement pnEl)) prefilledTokens  = pnEl.GetInt32();
                        if (timings.TryGetProperty("prompt_per_second", out JsonElement ppsEl) && ppsEl.ValueKind == JsonValueKind.Number)
                            prefillTokPerSec = ppsEl.GetDouble();
                    }

                    if (!chunk.RootElement.TryGetProperty("choices", out JsonElement choices) || choices.GetArrayLength() == 0) continue;

                    JsonElement choice = choices[0];

                    if (choice.TryGetProperty("finish_reason", out JsonElement frEl) && frEl.ValueKind != JsonValueKind.Null)
                        finishReason = frEl.GetString();

                    JsonElement delta = choice.GetProperty("delta");

                    // Wall-clock bucketing: the first delta of a request closes its prefill window; after
                    // that each delta ticks the thinking or typing clock (stalls tick neither — see TurnClock).
                    bool reasoningDelta = delta.TryGetProperty("reasoning_content", out JsonElement rcProbe)
                        && rcProbe.ValueKind == JsonValueKind.String && (rcProbe.GetString()?.Length ?? 0) > 0;
                    clock.Tick(reasoningDelta);

                    // Live progress so a long/runaway generation is visible in the log as it happens.
                    if (!QuietLogging && (DateTime.UtcNow - lastProgress).TotalSeconds >= 3)
                    {
                        lastProgress = DateTime.UtcNow;
                        int argChars = pendingCalls.Values.Sum(c => c.Args.Length);
                        string tail  = responseBuilder.Length > 0
                            ? responseBuilder.ToString()
                            : (pendingCalls.Count > 0 ? pendingCalls.Values.Last().Args.ToString() : "");
                        if (tail.Length > 120) tail = tail[^120..];
                        Shared.Logger.LogInformation("[{Agent}] ({Thread}) … decoding: {N} native call(s) [{Names}], {AC} arg chars, {CC} content chars | …{Tail}",
                            Name, thread.Key, pendingCalls.Count,
                            string.Join(",", pendingCalls.Values.Select(c => c.Name)),
                            argChars, responseBuilder.Length, tail.Replace("\n", "\\n"));

                        // Runaway-content guard: a weak model can degenerate into a character spiral (e.g. an
                        // escalating backslash/quote-escape mess) that never forms a valid tool call and would
                        // run to the token limit. If one non-whitespace character dominates the recent output,
                        // the generation is garbage — stop the stream and end the turn.
                        if (responseBuilder.Length > 2000)
                        {
                            string recent = responseBuilder.ToString();
                            recent = recent.Length > 600 ? recent[^600..] : recent;
                            (char domChar, double ratio) = DominantChar(recent);
                            if (ratio > 0.6 && !char.IsWhiteSpace(domChar))
                            {
                                Shared.Logger.LogWarning("[{Agent}] ({Thread}) content runaway: '{Char}' is {Pct}% of recent output — aborting generation.",
                                    Name, thread.Key, domChar == '\\' ? "\\\\" : domChar.ToString(), (int)(ratio * 100));
                                contentRunaway = true;
                            }
                        }
                    }
                    if (contentRunaway) break;

                    if (delta.TryGetProperty("reasoning_content", out JsonElement reasoning))
                    {
                        string? thinkDelta = reasoning.GetString();
                        if (!string.IsNullOrEmpty(thinkDelta) && !wasThinking)
                        {
                            if (!Think)
                                Shared.Logger.LogWarning("[{Agent}] ({Thread}) thinking chain detected — <|think_off|> may not be working.", Name, thread.Key);
                            else
                                Shared.Logger.LogInformation("[{Agent}] ({Thread}) reasoning engaged (thinking on).", Name, thread.Key);
                            wasThinking = true;
                        }
                        if (!string.IsNullOrEmpty(thinkDelta)) { reasoningChars += thinkDelta.Length; reasoningBuilder.Append(thinkDelta); }
                        if (liveReasoning is null) { liveReasoning = new TraceStep { Kind = "reasoning", Text = "" }; trace.Add(liveReasoning); }
                        liveReasoning.Text = reasoningBuilder.ToString(reasoningStartLen, reasoningBuilder.Length - reasoningStartLen);
                        // Reasoning never reaches the chat stream, so nothing would wake watching clients while
                        // the model thinks — poke them per delta so the DTI re-fetches the growing live step
                        // (the DTI's own 120ms debounce coalesces the refetches).
                        if (!chatHidden) thread.RaiseStreaming(thread.streamedText);
                        // Thinking budget (#35 v2): all thresholds compare the THINKING clock — time actually
                        // spent receiving reasoning — never wall-clock (a 223s prefill once consumed a 30s
                        // budget before the model produced a word). Overruns break to the /completion
                        // continuation (ContinueThinking), which re-feeds the reasoning + a cue and escalates
                        // 80% warn → 100% finish-sentence → 110% guided close → 120% forced </think>. Nothing
                        // flips enable_thinking mid-turn: a template flip invalidates the whole KV prefix.
                        // The 80% warning is gated to budgets ≥30s (on a tiny budget it would fire seconds
                        // before the 100% cue — pure spam) and the ARI_NO_WARNINGS A/B toggle; the 100%
                        // cue always fires regardless of budget size.
                        if (thinkDeadlineSec > 0 && pendingNudge is null)
                        {
                            double frac = clock.Thinking / thinkDeadlineSec;
                            if (frac >= 1.0 && !warned100)
                            {
                                warned100    = true;
                                pendingNudge = Nudge100;
                                Shared.Logger.LogInformation("[{Agent}] ({Thread}) thinking budget {Sec}s spent — sending finish-sentence cue ({RC} reasoning chars).",
                                    Name, thread.Key, thinkDeadlineSec, reasoningChars);
                            }
                            else if (warningsEnabled && thinkDeadlineSec >= 30 && frac >= 0.80 && !warned80)
                            {
                                warned80     = true;
                                pendingNudge = Nudge80;
                            }
                        }
                        // Grade 10 (unlimited budget, thinkDeadlineSec < 0): no deadline, but every 30s of
                        // thinking inject a time-awareness nudge so an open-ended thinker stays mindful.
                        if (thinkDeadlineSec < 0 && pendingNudge is null)
                        {
                            int el = (int)clock.Thinking;
                            if (el - lastPeriodicSec >= 30) { lastPeriodicSec = el; pendingNudge = PeriodicNudge(el); }
                        }
                    }
                    if (pendingNudge is not null) break;

                    if (delta.TryGetProperty("tool_calls", out JsonElement toolCallsEl))
                    {
                        if (responseBuilder.Length > 0)
                        {
                            string preText = responseBuilder.ToString().TrimEnd();
                            bool isLeakedToolCall = preText.Contains("<tool_call>") || preText.Contains("<function=")
                                || thread.tools.Keys.Any(k => preText.StartsWith(k, StringComparison.OrdinalIgnoreCase));
                            if (!isLeakedToolCall && preText.Length > 0)
                            {
                                contentBuilder.Append(preText + "\n");
                                if (liveText is not null) { trace.Remove(liveText); liveText = null; }
                                trace.Add(new TraceStep { Kind = "text", Text = preText });
                                if (!QuietLogging)
                                    Shared.Logger.LogInformation("[{Agent}] ({Thread}) \"{Text}\"", Name, thread.Key, preText);
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

                                    // Runaway guard: native tool-call args must be JSON. If the model leaks
                                    // text-format tool markers into them it has started looping / stuffing extra
                                    // calls inside this one — stop the stream now and salvage the first call.
                                    if (runawayCall is null && (call.Args.ToString().Contains("<tool_call") || call.Args.ToString().Contains("<function=")))
                                    {
                                        runawayCall = index;
                                        Shared.Logger.LogWarning("[{Agent}] ({Thread}) native tool-call runaway ({Tool}, {Len} arg chars — text-format leak) — aborting generation and salvaging.", Name, thread.Key, call.Name, call.Args.Length);
                                        break;
                                    }

                                    if (earlyAbort is null && call.Name == "edit_file" && !precheckedCalls.Contains(index))
                                    {
                                        precheckedCalls.Add(index);
                                        IEnumerable<string> pendingReadPaths = pendingCalls.Values
                                            .Where(pc => pc.Name == "read_file" || pc.Name == "preview_file")
                                            .Select(pc => ToolCallParser.TryExtractJsonString(pc.Args.ToString(), "path"))
                                            .Where(p => p is not null)!
                                            .Cast<string>();
                                        string? abortMsg = StreamEditPrecheck(thread, toolTurn, call.Name, call.Args.ToString(), pendingReadPaths, threadAtts, msgAtts);
                                        if (abortMsg is not null)
                                        {
                                            earlyAbort = (call.Id, call.Name, call.Args.ToString(), abortMsg);
                                            break;
                                        }
                                    }

                                    if (thread.tools.TryGetValue(call.Name, out var liveTool) && liveTool.StreamingDisplay is not null)
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
                        if (earlyAbort is not null || runawayCall is not null) break;
                        continue;
                    }

                    if (!delta.TryGetProperty("content", out JsonElement contentEl)) continue;
                    string? deltaText = contentEl.GetString();
                    if (!string.IsNullOrEmpty(deltaText))
                    {
                        deltaText = deltaText
                            .Replace("<|think_off|>", "")
                            .Replace("<|think_on|>",  "")
                            .Replace("<|tool_code_start|>", "")
                            .Replace("<|tool_code_end|>",   "")
                            .Replace("<|tool_call|>",       "");
                        if (string.IsNullOrEmpty(deltaText)) continue;
                        responseBuilder.Append(deltaText);
                        if (thread.LiveCall is { } lc) lc.EstimatedOutputTokens = responseBuilder.Length / CHARS_PER_TOKEN;
                        if (onDelta is not null)
                        {
                            const string AriPrefix = "ARI: ";
                            string accumulated = responseBuilder.ToString();
                            string visible = accumulated.Length < AriPrefix.Length
                                ? (accumulated.StartsWith(AriPrefix[..accumulated.Length], StringComparison.OrdinalIgnoreCase) ? "" : accumulated)
                                : (accumulated.StartsWith(AriPrefix, StringComparison.OrdinalIgnoreCase) ? accumulated[AriPrefix.Length..] : accumulated);
                            // Show a live "Editing <file> +N/-M" card while the model streams an edit_file/
                            // write_file text tool call, instead of frozen narration; every OTHER tool call's
                            // raw <tool_call>/<function=…> text (complete or still-streaming, including a
                            // half-typed opening tag) is stripped so it never flashes as prose in the client
                            // (#78). View-only transforms of the streamed text; responseBuilder and the
                            // parse/execute path are untouched.
                            string liveView = StripStreamingToolText(InjectLiveToolCards(visible));
                            if (liveText is null && liveView.Trim().Length > 0) { liveText = new TraceStep { Kind = "text", Text = "" }; trace.Add(liveText); }
                            if (liveText is not null) liveText.Text = liveView;
                            await onDelta(contentBuilder.ToString() + liveView);
                        }
                    }
                }
            }

            // If a budget warning fired, the chat stream stopped mid-thought. Continue the chain via raw /completion
            // (re-feed reasoning + the nudge, keep thinking), then fall through to normal post-stream parsing. The
            // answer lands in responseBuilder (parsed for tools as usual); reasoning stays hidden (debug trace only).
            if (pendingNudge is not null)
            {
                Shared.Logger.LogInformation("[{Agent}] ({Thread}) budget warning at {RC} reasoning chars — continuing via /completion.", Name, thread.Key, reasoningChars);
                await ContinueThinking(messages, body, httpClient, thread, reasoningBuilder, responseBuilder, contentBuilder,
                    pendingNudge, clock, thinkDeadlineSec, warned80, warned100, lastPeriodicSec, onDelta, ct);
                pendingNudge   = null;
                finishReason ??= "stop";
                reasoningChars = reasoningBuilder.Length;
            }

            // Deep-inspection trace: finalize THIS request's reasoning step (created live at the first
            // reasoning delta; ContinueThinking may have appended more since the last delta tick).
            if (reasoningBuilder.Length > reasoningStartLen)
            {
                if (liveReasoning is null) { liveReasoning = new TraceStep { Kind = "reasoning" }; trace.Add(liveReasoning); }
                liveReasoning.Text = reasoningBuilder.ToString(reasoningStartLen, reasoningBuilder.Length - reasoningStartLen);
            }
            // The provisional live-text step gives way to the parsed text steps recorded below.
            if (liveText is not null) { trace.Remove(liveText); liveText = null; }

            if (!QuietLogging)
            {
                int doneArgChars = pendingCalls.Values.Sum(c => c.Args.Length);
                Shared.Logger.LogInformation("[{Agent}] ({Thread}) ← stream done: finish={FR}, completion_tokens={CT}, reasoning_chars={RC}, {PC} native call(s) [{Names}], {CC} content chars, {AC} arg chars",
                    Name, thread.Key, finishReason ?? "null", completionTokens, reasoningChars, pendingCalls.Count,
                    string.Join(",", pendingCalls.Values.Select(c => c.Name)), responseBuilder.Length, doneArgChars);
                if (responseBuilder.Length > 0)
                {
                    string snip = responseBuilder.ToString();
                    Shared.Logger.LogInformation("[{Agent}] ({Thread}) ← content: {Snip}",
                        Name, thread.Key, (snip.Length > 400 ? snip[..400] + "…" : snip).Replace("\n", "\\n"));
                }
            }

            // The stream degenerated into a character spiral. Discard the garbage and end the turn —
            // there's no salvageable tool call, and continuing would just repeat the spiral.
            if (contentRunaway)
            {
                responseBuilder.Clear();
                contentBuilder.Append("\n\n_Stopped — the model's output ran away repeating characters._");
                if (onDelta is not null) await onDelta(contentBuilder.ToString());
                break;
            }

            if (earlyAbort is not null)
            {
                var (aId, aName, aArgs, aErr) = earlyAbort.Value;
                string? aPath  = ToolCallParser.TryExtractJsonString(aArgs, "path");
                string  safeArgs = JsonSerializer.Serialize(new { path = aPath ?? "" });

                if (responseBuilder.Length > 0)
                {
                    string preText = responseBuilder.ToString().TrimEnd();
                    if (preText.Length > 0) contentBuilder.Append(preText + "\n");
                }

                messages.Add(new { role = "assistant", tool_calls = new[]
                    { new { id = aId, type = "function", function = new { name = aName, arguments = safeArgs } } } });
                messages.Add(new { role = "tool", tool_call_id = aId, name = aName, content = aErr });
                if (thread.liveCallInfo is { } lcAbort) lcAbort.EstimatedInputTokens += aErr.Length / CHARS_PER_TOKEN;

                string aLabel = aPath is not null ? System.IO.Path.GetFileName(aPath.Trim('"', '\'', ' ', '\\')) : "";
                contentBuilder.Append($"<!--ari-tool-error:{aName}:{aLabel}:{ToolCallParser.EscapeLabel(aErr)}-->");
                if (onDelta is not null) await onDelta(contentBuilder.ToString());

                toolCallCount++;
                Degrade();
                continue;
            }

            if (pendingCalls.Count == 0 && responseBuilder.Length > 0)
            {
                string rawResponse = responseBuilder.ToString();
                if (rawResponse.Contains("<|tool_code_start|>") || rawResponse.Contains("<|tool_call|>"))
                {
                    consecutiveFallbacks++;
                    Degrade();
                    if (consecutiveFallbacks > 3)
                        throw new LlmRequestFailedException($"Model stuck in tool_code_start fallback loop ({consecutiveFallbacks} consecutive) — aborting.");
                    Shared.Logger.LogWarning("[{Agent}] ({Thread}) model used <|tool_code_start|> format — cannot parse, injecting correction.", Name, thread.Key);
                    messages.Add(new { role = "assistant", content = rawResponse.Replace("<|tool_code_start|>", "").Replace("<|tool_code_end|>", "").Replace("<|tool_call|>", "").Trim() });
                    messages.Add(new { role = "user", content = "[System: Your last response contained tool call markers (<|tool_code_start|> or <|tool_call|>) with no parseable arguments. Do not use these markers. Issue tool calls using only the proper JSON function-call format.]" });
                    responseBuilder.Clear();
                    continue;
                }

                // Guard against a tool call truncated before its closing tag (e.g. hit max_tokens): if an
                // opening <tool_call> has no matching close, add one so ParseTextCalls can still match it.
                if (textTools && responseBuilder.ToString().Contains("<tool_call>") && !responseBuilder.ToString().Contains("</tool_call>"))
                    responseBuilder.Append("\n</tool_call>");

                List<ToolCallParser.Call>? textCalls = ToolCallParser.ParseTextCalls(responseBuilder.ToString());
                if (textCalls is not null)
                {
                    if (!textTools)
                    {
                        // Unexpected text format from a native-tools model — treat as a degraded fallback.
                        consecutiveFallbacks++;
                        Degrade();
                        if (consecutiveFallbacks > 3)
                            throw new LlmRequestFailedException($"Model stuck in text tool call fallback loop ({consecutiveFallbacks} consecutive) — aborting.");
                        Shared.Logger.LogWarning("[{Agent}] ({Thread}) model used text tool call format — parsing fallback.", Name, thread.Key);
                    }
                    else
                    {
                        // Expected path: preserve any narration the model wrote before the tool call so the
                        // user still sees "Now I'll look at…" style updates (reasoning in <think> is dropped).
                        string rb    = responseBuilder.ToString();
                        int    tcIdx = rb.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
                        if (tcIdx > 0)
                        {
                            string pre = System.Text.RegularExpressions.Regex.Replace(
                                rb[..tcIdx], "<think>.*?</think>", "",
                                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            pre = pre.Replace("<think>", "").Replace("</think>", "").Trim();
                            if (pre.Length > 0) { contentBuilder.Append(pre); trace.Add(new TraceStep { Kind = "text", Text = pre }); }
                        }
                    }
                    int fakeIndex = 0;
                    foreach (ToolCallParser.Call c in textCalls)
                        pendingCalls[fakeIndex++] = (c.Id, c.Name, new StringBuilder(c.Args));

                    responseBuilder.Clear();
                    finishReason = "tool_calls";
                }

                if (pendingCalls.Count == 0 && thread.tools.Count > 0)
                {
                    ToolCallParser.XmlParse? xml = ToolCallParser.ParseXmlCalls(responseBuilder.ToString(), thread.tools.Keys);
                    if (xml is not null)
                    {
                        consecutiveFallbacks++;
                        Degrade();
                        if (consecutiveFallbacks > 3)
                            throw new LlmRequestFailedException($"Model stuck in XML tool call fallback loop ({consecutiveFallbacks} consecutive) — aborting.");
                        Shared.Logger.LogWarning("[{Agent}] ({Thread}) model used Qwen3 XML tool call format — parsing fallback.", Name, thread.Key);

                        xmlFallbackOriginalText = responseBuilder.ToString();

                        if (xml.FirstIndex > 0)
                        {
                            string preXml = xmlFallbackOriginalText[..xml.FirstIndex].TrimEnd();
                            contentBuilder.Append(preXml);
                            if (preXml.Length > 0) trace.Add(new TraceStep { Kind = "text", Text = preXml });
                        }

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
                foreach (var key in pendingCalls.Keys)
                {
                    var (id, name, args) = pendingCalls[key];
                    string original = args.ToString();
                    string raw      = original;
                    // Salvage a runaway native call: the model leaked text-format markers into the JSON
                    // args, so keep only the first valid call and discard the looping tail.
                    if (raw.Contains("<tool_call") || raw.Contains("<function="))
                    {
                        raw = ToolCallParser.SalvageNativeArgs(raw);
                        Shared.Logger.LogInformation("[{Agent}] ({Thread}) salvaged runaway args for '{Tool}' → {Args}", Name, thread.Key, name, raw);
                    }
                    string stripped = ToolCallParser.StripThinkLeaks(raw);
                    string repaired = ToolCallParser.RepairArgs(stripped);

                    if (stripped != raw)
                        Shared.Logger.LogWarning("[{Agent}] ({Thread}) Stripped <think> leakage from args for tool '{Tool}'.", Name, thread.Key, name);
                    if (repaired != stripped)
                        Shared.Logger.LogWarning("[{Agent}] ({Thread}) Repaired malformed JSON args for tool '{Tool}'.", Name, thread.Key, name);

                    // Write back whenever salvage/strip/repair changed the original — comparing against
                    // the original (not the post-salvage `raw`) so a fully-cleaned salvage still persists.
                    if (repaired != original)
                        pendingCalls[key] = (id, name, new StringBuilder(repaired));
                }

                bool isXmlFallback = pendingCalls.Values.Any(c => c.Id.StartsWith("fallback_xml_"));

                toolCallCount += pendingCalls.Count;

                if (isXmlFallback)
                {
                    messages.Add(new { role = "assistant", content = xmlFallbackOriginalText ?? "" });
                }
                else
                {
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

                StringBuilder? xmlResultsMsg = isXmlFallback
                    ? new StringBuilder("Here are the results of the tool calls you made:\n\n")
                    : null;

                OnToolBatchStart(toolTurn);

                HashSet<string> readOnlyTools = new(StringComparer.OrdinalIgnoreCase)
                    { "read_file", "search_files", "list_directory", "find_files" };
                Dictionary<int, Task<string>> prelaunched = new();
                if (pendingCalls.Count > 1)
                    foreach (var (idx, c) in pendingCalls)
                        if (readOnlyTools.Contains(c.Name) && thread.tools.TryGetValue(c.Name, out var roTool))
                            prelaunched[idx] = roTool.Execute(c.Args.ToString());

                bool productiveBatch = false; // set true when a tool returns new info or mutates a file
                foreach (var (callIndex, call) in pendingCalls)
                {
                    string argsJson = call.Args.ToString();
                    string result;

                    trace.Add(new TraceStep { Kind = "tool_call", Name = call.Name, Args = argsJson });

                    // Code-specific pre-execute guards (dedup / nudges / build-before-test / command cache).
                    // Returns a short-circuit result, or null to run the tool. See Code.ToolLoop.cs.
                    string? guard = PreToolGuard(thread, toolTurn, call.Name, call.Id, argsJson);
                    if (guard is not null)
                    {
                        result = guard;
                    }
                    else if (thread.tools.TryGetValue(call.Name, out var tool))
                    {
                        // The ACTIVE card marker (present tense). Keep a live streaming marker (if any) as the active
                        // card so its diff isn't lost; otherwise append the tool's Display marker.
                        string? activeMarker = null;
                        if (streamingMarkers.TryGetValue(callIndex, out string? prevStreamMarker))
                            activeMarker = prevStreamMarker;
                        else if (tool.Display is not null)
                        {
                            activeMarker = tool.Display(argsJson);
                            contentBuilder.Append(activeMarker);
                            if (onDelta is not null) await onDelta(contentBuilder.ToString());
                        }

                        // Let a long-running tool stream rendered display into THIS response while it executes
                        // (spawn_coder mirrors a Coder sub-agent's edits inline). Cleared immediately after.
                        thread.ToolDisplaySink = async chunk =>
                        {
                            contentBuilder.Append(chunk);
                            if (onDelta is not null) await onDelta(contentBuilder.ToString());
                        };
                        try
                        {
                            result = prelaunched.TryGetValue(callIndex, out Task<string>? pre)
                                ? await pre
                                : await tool.Execute(argsJson);
                        }
                        finally { thread.ToolDisplaySink = null; }

                        // Code-specific post-processing (cache, circuit breaker, edit/build tracking). May throw
                        // to abort the turn. See Code.ToolLoop.cs.
                        result = PostToolProcess(thread, toolTurn, call.Name, argsJson, result);

                        // read_file auto-diverted to a preview (result starts with "[preview:", not an error): relabel
                        // its card to a done "Previewed" card and skip the flip (the preview already completed).
                        if (activeMarker is not null && call.Name == "read_file" && result.StartsWith("[preview:", StringComparison.Ordinal))
                        {
                            string pf = "";
                            try { using JsonDocument pvd = JsonDocument.Parse(argsJson); pf = System.IO.Path.GetFileName((pvd.RootElement.TryGetProperty("path", out JsonElement ppe) ? ppe.GetString() : null)?.Trim('"', '\'', ' ') ?? ""); }
                            catch { /* ignore */ }
                            ReplaceInBuilder(contentBuilder, activeMarker, $"<!--ari-tool-done:preview_file:{pf.Replace("--", "&#45;&#45;")}-->");
                            if (onDelta is not null) await onDelta(contentBuilder.ToString());
                            activeMarker = null;
                        }

                        // Flip the card to its done (past-tense) form once the tool returns (unless it errored).
                        // Card.Flip() is the single flip mechanism; each card renders its own done form — a diff card
                        // keeps its +/- badges (nothing lost), simple cards flip Reading→Read, Delegating→Delegated…
                        if (activeMarker is not null && !ToolCallParser.IsError(result))
                        {
                            Card? doneCard = ContentBlock.Parse(activeMarker).OfType<Card>().FirstOrDefault();
                            if (doneCard is not null)
                            {
                                doneCard.Flip();
                                string done = doneCard.Render();
                                if (!string.Equals(done, activeMarker, StringComparison.Ordinal))
                                {
                                    ReplaceInBuilder(contentBuilder, activeMarker, done);
                                    if (onDelta is not null) await onDelta(contentBuilder.ToString());
                                }
                            }
                        }

                        if (ToolCallParser.IsError(result))
                            Shared.Logger.LogError("[{Agent}] ({Thread}) Tool '{Tool}' failed: {Error}", Name, thread.Key, call.Name, result);
                        else if (tool.DisplayAfter is not null)
                        {
                            contentBuilder.Append(tool.DisplayAfter(argsJson));
                            if (onDelta is not null) await onDelta(contentBuilder.ToString());
                        }
                    }
                    else
                    {
                        result = $"[Error: tool '{call.Name}' is not registered]";
                        Shared.Logger.LogError("[{Agent}] ({Thread}) Model called unknown tool '{Tool}'", Name, thread.Key, call.Name);
                    }

                    // A guard message ("[System:") or an error renders as an inline tool-error card.
                    if (result.StartsWith("[System:", StringComparison.Ordinal) || ToolCallParser.IsError(result))
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
                        contentBuilder.Append($"<!--ari-tool-error:{call.Name}:{label}:{ToolCallParser.EscapeLabel(result)}-->");
                        if (onDelta is not null) await onDelta(contentBuilder.ToString());
                    }

                    toolResults.Add(result);
                    trace.Add(new TraceStep { Kind = "tool_result", Name = call.Name, Text = result });
                    // Progress = a tool returned real content or mutated a file. Guard nags start with "[System:"
                    // and errors with "[Error:"; a batch of only those is "no progress" (feeds the loop-breaker).
                    if (!result.StartsWith("[System:", StringComparison.OrdinalIgnoreCase) && !ToolCallParser.IsError(result))
                        productiveBatch = true;

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
                        if (thread.liveCallInfo is { } lc) lc.EstimatedInputTokens += result.Length / CHARS_PER_TOKEN;
                        AfterToolAppended(toolTurn, messages, call.Name, call.Id, argsJson, result, addedIndex);
                    }
                }

                if (isXmlFallback)
                {
                    string xmlMsg = xmlResultsMsg!.ToString().TrimEnd();
                    messages.Add(new { role = "user", content = xmlMsg });
                    if (thread.liveCallInfo is { } lc) lc.EstimatedInputTokens += xmlMsg.Length / CHARS_PER_TOKEN;
                }

                contentBuilder.Append("<!--ari-batch-end-->");
                if (onDelta is not null) await onDelta(contentBuilder.ToString());

                // Loop-breaker: a weak model can call tools forever without progressing (e.g. re-reading a
                // file it already read). The per-tool nags only scold; nothing terminates. Under the text
                // protocol MaxToolCalls doesn't help either — text calls still execute after tools are
                // "exhausted". So after enough consecutive no-progress batches, end the turn outright.
                if (OnBatchEndShouldBreak(thread, toolTurn, productiveBatch))
                {
                    contentBuilder.Append("\n\n_Stopped — repeated tool calls were not making progress._");
                    if (onDelta is not null) await onDelta(contentBuilder.ToString());
                    break;
                }

                // Only nag about format when NOT on the text protocol — under it, text-format tool calls
                // (fallback_* ids) are the expected, correct form, not an error to be corrected.
                bool wasFallback = !textTools && pendingCalls.Values.Any(c => c.Id.StartsWith("fallback_"));
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

            bool toolsStillAvailable = !toolTurn.ForceNoMoreTools && !(MaxToolCalls > 0 && toolCallCount >= MaxToolCalls);
            if (pendingCalls.Count == 0 && toolsStillAvailable && continueNudges < 2)
            {
                string tail = responseBuilder.ToString().TrimEnd();
                bool promisesAction = tail.Length > 0 && (
                    tail.EndsWith(":")
                    || System.Text.RegularExpressions.Regex.IsMatch(tail,
                        @"(?i)\b(let me|let's|i'll|i will|i'm going to|i need to|now i'll|first,? i|next,? i)\b[^.!?]{0,100}$"));
                bool mentionsVerb = System.Text.RegularExpressions.Regex.IsMatch(tail,
                    @"(?i)\b(read|check|run|build|test|look|examine|open|search|edit|create|add|update|fix|verify|inspect|modify|write|review|rebuild|re-?run)\b");
                // Empty-turn-after-reasoning: in thinking mode this model sometimes spends a whole turn inside
                // the reasoning block and stops with NO answer and NO tool call (responseBuilder empty). That
                // is never a real completion — re-prompt it to actually act. (The narrate-without-acting case
                // above only fires when there IS content; this catches the zero-content case.)
                // Degenerate-placeholder turn: the model occasionally answers with a literal template
                // variable instead of content — e.g. its entire output is "${plan}" or "{summary}". Treat it
                // like an empty turn (it is one, semantically) so the nudge makes it write the real thing.
                bool placeholderTurn = tail.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(
                    tail, @"^\$?\{[\w .-]{1,60}\}$");
                bool emptyTurn = tail.Length == 0 || placeholderTurn;
                // Calls written INSIDE the <think> block land in reasoning_content and are never parsed or
                // executed — a whole turn can be "spent" on tool calls that went nowhere. Tell the model
                // exactly what happened so its retry re-issues them as the answer.
                bool callsInThinking = emptyTurn && reasoningBuilder.Length > reasoningStartLen &&
                    reasoningBuilder.ToString(reasoningStartLen, reasoningBuilder.Length - reasoningStartLen)
                        .Contains("<tool_call>", StringComparison.OrdinalIgnoreCase);
                if ((promisesAction && mentionsVerb) || emptyTurn)
                {
                    continueNudges++;
                    string why = callsInThinking
                        ? "You wrote your tool calls INSIDE your thinking block — tool calls made while thinking are NOT executed. Nothing ran"
                        : placeholderTurn
                        ? $"Your entire answer was the literal placeholder \"{tail}\" — that is not content; write the actual text it stands for"
                        : emptyTurn
                        ? "Your reasoning finished but you produced no answer and no tool call — the turn was empty"
                        : "You described an action but didn't perform it — no tool call was made";
                    Shared.Logger.LogInformation("[{Agent}] ({Thread}) premature-stop nudge ({Kind}).", Name, thread.Key,
                        callsInThinking ? "tool-calls-inside-thinking" : placeholderTurn ? "placeholder-answer" : emptyTurn ? "empty-turn-after-reasoning" : "narrated-no-action");
                    messages.Add(new { role = "user", content =
                        $"[System: {why}. Don't stop here: take the next concrete action now — issue the tool call AFTER your thinking ends, as your answer (and keep working until the task is done AND the project builds), or if you are genuinely finished, give the user a short summary of what you changed. Do not reply with nothing, and do not repeat a tool call you already made.]" });
                    responseBuilder.Clear();
                    continue;
                }
            }

            break;
        }

        sw.Stop();
        string responseText = contentBuilder.Length > 0
            ? contentBuilder.ToString() + responseBuilder.ToString()
            : responseBuilder.ToString();
        if (responseText.StartsWith("ARI: ", StringComparison.OrdinalIgnoreCase))
            responseText = responseText["ARI: ".Length..];
        responseText = responseText
            .Replace("<|think_off|>", "")
            .Replace("<|think_on|>", "")
            .Trim();
        if (string.IsNullOrWhiteSpace(responseText))
            throw new LlmRequestFailedException("LLM response was empty.");

        // Closing prose for the trace — ONLY the trailing summary (responseBuilder), not the whole turn. Prose
        // the model wrote earlier (e.g. the plan, before the first tool call) was already traced as its own
        // 'text' step at its real position, so it shows before the coders instead of merging in here.
        string traceText = System.Text.RegularExpressions.Regex.Replace(responseBuilder.ToString(), @"<!--ari-[\s\S]*?-->", "");
        traceText = System.Text.RegularExpressions.Regex.Replace(traceText, "<div class=\"tool-use\">[\\s\\S]*?</div>", "");
        traceText = traceText.Replace("<|think_off|>", "").Replace("<|think_on|>", "").Trim();
        if (traceText.StartsWith("ARI: ", StringComparison.OrdinalIgnoreCase)) traceText = traceText["ARI: ".Length..];
        if (traceText.Length > 0) trace.Add(new TraceStep { Kind = "text", Text = traceText.Trim() });

        double elapsed   = sw.Elapsed.TotalSeconds;
        double tokPerSec = completionTokens > 0 ? completionTokens / elapsed : 0;

        if (!QuietLogging)
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
        if (!string.IsNullOrEmpty(recallNotes)) noteParts.Add(recallNotes.Trim());
        if (toolResults.Count > 0)              noteParts.Add(string.Join("\n\n", toolResults).TrimEnd());
        string? combinedNotes = noteParts.Count > 0 ? string.Join("\n\n", noteParts) : null;

        thread.liveCallInfo = null;

        ariResponse.Content                   = ContentBlock.Parse(responseText);
        ariResponse.Data.DebugResponseText         = responseText;
        ariResponse.Reasoning                 = reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null;
        ariResponse.ThinkingSeconds           = clock.Thinking;
        ariResponse.PrefillSeconds            = clock.Prefill;
        ariResponse.TypingSeconds             = clock.Typing;
        ariResponse.TotalSeconds              = elapsed;
        ariResponse.RecallNotes               = combinedNotes;
        ariResponse.ContextSummary            = contextSummary;
        ariResponse.Data.CompletionTokens          = completionTokens;
        ariResponse.Data.OutputTokenLimit          = maxTokens > 0 ? maxTokens : 0;
        ariResponse.Data.PromptTokens              = promptTokens;
        ariResponse.Data.PrefilledPromptTokens     = prefilledTokens;
        ariResponse.Data.ContextTokenLimit         = MaxContextTokens;
        ariResponse.Data.HadImageAttachments       = hadImages;
        ariResponse.Data.EstimatedTextPromptTokens = estimatedTextTokens;
        ariResponse.Data.ImageTokenLimit           = 0;
        ariResponse.State                     = State.Complete;
        ariResponse.StreamText               = null;
        thread.streamingResponse              = null;
        thread.RaiseStreamingFinished();

        thread.ariRepliedAt = DateTime.UtcNow;
        thread.State = ThreadState.Idle;
        thread.inactivityTimer?.Dispose();
        thread.inactivityTimer = new Timer(_ =>
        {
            if (thread.State != ThreadState.Idle) return;
            thread.State = ThreadState.Dormant;
            thread.RaiseBecameInactive();
        }, null, thread.InactivityThreshold, Timeout.InfiniteTimeSpan);

        thread.RaiseExchangeCompleted(prompt, responseText);

        if (MemoryLimit > 0 && thread.History.Count >= MemoryLimit)
        {
            int engramInterval = Math.Max(1, MemoryLimit / 2);
            if (thread.History.Count == MemoryLimit || thread.History.Count % engramInterval == 0)
                thread.RaiseBufferFull();
        }

        return responseText;
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the textual tool catalog appended to the system prompt. We deliberately do NOT send the
    /// native `tools` field to llama-server for this model. Qwen3.6's chat template trains the model to
    /// emit tool calls as &lt;tool_call&gt;&lt;function=name&gt;&lt;parameter=..&gt; TEXT; llama.cpp 9430 only
    /// intermittently re-parses that into native tool_calls, and when it half-parses the XML tail leaks
    /// into the JSON arguments (the "runaway"). Advertising tools as text and parsing the response
    /// ourselves (ParseTextCalls) makes every tool call deterministic. Mirrors the model template's
    /// own tool-advertisement block so the format the model sees is exactly what it was trained on.
    /// </summary>
    private static string BuildToolCatalog(object[] schemas)
    {
        StringBuilder sb = new();
        sb.Append("\n\n# Tools\n\nYou have access to the following functions:\n\n<tools>");
        foreach (object schema in schemas)
        {
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(schema));
            JsonElement fn = doc.RootElement.TryGetProperty("function", out JsonElement f) ? f : doc.RootElement;
            sb.Append('\n').Append(fn.GetRawText());
        }
        sb.Append("\n</tools>\n\n");
        sb.Append(
            "To call a function, reply with ONLY this format and NOTHING after it:\n" +
            "<tool_call>\n<function=FUNCTION_NAME>\n<parameter=PARAM_NAME>\nVALUE\n</parameter>\n</function>\n</tool_call>\n" +
            "Rules:\n" +
            "- Put the <tool_call> block at the start of a new line with no indentation.\n" +
            "- One <parameter=NAME> block per argument; a value may span multiple lines.\n" +
            "- Use the XML format above exactly — do NOT emit JSON for the call.\n" +
            "- Stop immediately after </tool_call>; the tool result will be provided to you.\n" +
            "- When you are done and need no tool, reply normally with your final answer.");
        return sb.ToString();
    }

    // Matches an edit_file/write_file text tool call (complete, or still streaming to end-of-string).
    private static readonly System.Text.RegularExpressions.Regex LiveEditRe = new(
        @"<tool_call>\s*<function=(edit_file|write_file)>(.*?)(?:</tool_call>|\z)",
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Replaces an in-progress (or just-completed) edit_file/write_file text tool call in a streamed chunk
    /// with a live "active" tool-card marker (file + line counts), so the UI shows what is being edited as
    /// the model streams it rather than a frozen "thinking". Other tool calls are left for the UI to strip.
    /// The added count grows as new_string/content streams in; removed comes from start_line/end_line.
    /// </summary>
    private static string InjectLiveToolCards(string text)
    {
        if (!text.Contains("<function=edit_file", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("<function=write_file", StringComparison.OrdinalIgnoreCase))
            return text;

        return LiveEditRe.Replace(text, m =>
        {
            string name = m.Groups[1].Value.ToLowerInvariant();
            string body = m.Groups[2].Value;
            string? path = LiveParam(body, "path");
            if (path is null) return m.Value; // path not streamed yet — leave for the UI to strip
            string label = System.IO.Path.GetFileName(path.Trim()).Replace("--", "&#45;&#45;");
            string? content = LiveParam(body, name == "write_file" ? "content" : "new_string");
            int added = string.IsNullOrEmpty(content) ? 0 : content.Split('\n').Length;
            int removed = 0;
            if (int.TryParse(LiveParam(body, "start_line"), out int s) &&
                int.TryParse(LiveParam(body, "end_line"),   out int e) && e >= s)
                removed = e - s + 1;
            return $"<!--ari-tool-start:{name}:{label}|+{added}|-{removed}-->";
        });
    }

    // Complete or trailing-partial text tool calls in the STREAMED view. Ordered alternation: closed
    // blocks first, then an unterminated block running to end-of-string (the still-streaming case).
    private static readonly System.Text.RegularExpressions.Regex StreamToolTextRe = new(
        @"<tool_call>[\s\S]*?</tool_call>|<tool_call>[\s\S]*$|<function=[^>]*>[\s\S]*?</function[^>]*>|<function=[^>]*>[\s\S]*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // Opening tags a leak can start with — a bare partial prefix of one of these at the very end of the
    // stream ("<tool_ca") is held back until enough arrives to classify it.
    private static readonly string[] ToolTagPrefixes = { "<tool_call", "</tool_call", "<function=", "</function", "<parameter=", "</parameter" };

    /// <summary>
    /// View-only strip of text-protocol tool-call XML from the streamed text (#78): complete blocks,
    /// a block still streaming to end-of-string, and a partially-typed opening tag at the very tail
    /// (e.g. "&lt;tool_ca") — without this the raw call flashes as prose in every client until the
    /// post-stream parser strips it. The underlying builders are untouched.
    /// </summary>
    private static string StripStreamingToolText(string text)
    {
        if (text.IndexOf('<') < 0) return text;
        text = StreamToolTextRe.Replace(text, "");

        int lt = text.LastIndexOf('<');
        if (lt >= 0)
        {
            string tail = text[lt..];
            if (!tail.Contains('>'))
                foreach (string tag in ToolTagPrefixes)
                    if (tag.StartsWith(tail, StringComparison.OrdinalIgnoreCase) || tail.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                        return text[..lt];
        }
        return text;
    }

    /// <summary>Extracts a parameter value from a possibly-incomplete text tool-call body (up to
    /// &lt;/parameter&gt; if closed, else the partial trailing value). Null if the parameter hasn't started.</summary>
    private static string? LiveParam(string body, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(body,
            $@"<parameter={System.Text.RegularExpressions.Regex.Escape(key)}>\s*(.*?)\s*(?:</parameter>|\z)",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string ExtractLogText(string content) =>
        string.Concat(ContentBlock.Parse(content).OfType<TextBlock>().Select(b => b.Text))
            .Replace("<!--ari-batch-end-->", "")
            .Trim();

    /// <summary>Most-frequent character in s and the fraction of s it makes up — used to detect a
    /// degenerate repeated-character spiral (e.g. runaway backslash escaping) in streamed output.</summary>
    private static (char Char, double Ratio) DominantChar(string s)
    {
        if (string.IsNullOrEmpty(s)) return ('\0', 0);
        Dictionary<char, int> counts = new();
        foreach (char c in s) counts[c] = counts.TryGetValue(c, out int n) ? n + 1 : 1;
        KeyValuePair<char, int> top = counts.MaxBy(kv => kv.Value);
        return (top.Key, (double)top.Value / s.Length);
    }

    private static string? ContentOf(object m) => m.GetType().GetProperty("content")?.GetValue(m) as string;

    private static void CompactToolOutput(List<object> messages, List<(int Index, string CallId, string Name)> slots, int maxContextTokens)
    {
        if (maxContextTokens <= 0) return;

        long trigger = (long)(maxContextTokens * (long)CHARS_PER_TOKEN * COMPACT_RATIO_HIGH);
        long target  = (long)(maxContextTokens * (long)CHARS_PER_TOKEN * COMPACT_RATIO_LOW);
        long total   = 0;
        foreach (object m in messages) total += ContentOf(m)?.Length ?? 0;

        // Only compact once we ACTUALLY exceed the trigger. Stubbing tool outputs rewrites the middle of
        // the message array, which (1) invalidates the server's KV prefix cache and forces a full
        // re-prefill of the whole context each step (~125s on a large context), and (2) throws away file
        // contents the model just read, forcing wasteful re-reads. When triggered, stub all the way down
        // to the LOW watermark rather than just below the trigger — otherwise a session hovering at the
        // budget stubs one more output (and re-prefills) almost every turn.
        if (total <= trigger) return;

        int stubbable = slots.Count - COMPACT_KEEP_RECENT;
        for (int i = 0; i < stubbable && total > target; i++)
        {
            (int idx, string callId, string name) = slots[i];
            if (idx < 0 || idx >= messages.Count) continue;
            string? cur = ContentOf(messages[idx]);
            if (cur is null || cur.Length < 200) continue;
            string stub = $"[Earlier {name} output omitted to save context — re-run the tool if you need it again.]";
            messages[idx] = new { role = "tool", tool_call_id = callId, name, content = stub };
            total -= cur.Length - stub.Length;
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

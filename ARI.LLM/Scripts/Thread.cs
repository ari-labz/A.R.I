using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal enum ThreadState { Active, Inactive, Dormant, Deleted }

internal class Thread
{
    private const int MIN_INACTIVITY_TIMER     = 30;
    private const int MIN_DELETION_TIMER       = 15;
    private const int MIN_INACTIVITY_THRESHOLD = 1;
    private const int MAX_TOOL_CALLS           = 10;

    private readonly Agent      agent;
    private readonly string     threadKey;
    private readonly HttpClient httpClient;

    internal readonly List<ThreadItem> History;
    private readonly int shortTermMemoryLimit;
    private readonly int maxContextTokens;

    private readonly Dictionary<string, (object Schema, Func<string, Task<string>> Execute)> tools = new();

    // ── Lifecycle ──────────────────────────────────────────────────────────────
    private ThreadState             state           = ThreadState.Active;
    private readonly List<TimeSpan> responseSamples = new();
    private DateTime                ariRepliedAt    = DateTime.MinValue;
    private Timer?                  inactivityTimer;
    private Timer?                  dormantTimer;

    internal ThreadState State => state;

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

    // Volatile so the control-panel polling thread always sees the latest value written
    // by the streaming thread (critical on ARM / Apple Silicon where the memory model is weak).
    private volatile LiveCallInfo? _liveCall;
    internal LiveCallInfo? LiveCall => _liveCall;

    /// <summary>
    /// Sets a preliminary LiveCall before the actual LLM call starts (e.g. during memory recall).
    /// SendPromptCore will replace it with a fully-populated one once messages are built.
    /// </summary>
    /// <summary>Called by LlmService to inject a pre-created LiveCallInfo (already stored in its ConcurrentDictionary).</summary>
    internal void SetLiveCall(LiveCallInfo liveCall) => _liveCall = liveCall;

    internal void SignalProcessingStarted()
    {
        if (_liveCall is null)
            _liveCall = new LiveCallInfo(agent.Name, threadKey, 0, agent.MaxTokens, maxContextTokens, agent.MaxImageTokens);
    }

    // ── Attachments ────────────────────────────────────────────────────────────
    private readonly List<Attachment> attachments        = new();
    private readonly List<Attachment> pendingMessageAtts = new();

    internal string? PlatformContext { get; init; }

    internal DateTime LastMessageAt { get; private set; } = DateTime.MinValue;

    internal event Action? Updated;
    internal event Action? BufferFull;
    internal event Action<string, string>? ExchangeCompleted;
    internal event Action? BecameInactive;
    internal event Action? Deleted;

    // ── Constructor ─────────────────────────────────────────────────────────────

    internal Thread(Agent agent, string threadKey, string? platformContext = null, int shortTermMemoryLimit = 0, int maxContextTokens = 0)
    {
        this.agent                = agent;
        this.threadKey            = threadKey;
        this.shortTermMemoryLimit = shortTermMemoryLimit;
        this.maxContextTokens     = maxContextTokens;
        httpClient                = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        History                   = new List<ThreadItem>();
        PlatformContext           = platformContext;
    }

    // ── Tool registration ───────────────────────────────────────────────────────

    internal void RegisterTool(string name, object schema, Func<string, Task<string>> executor)
        => tools[name] = (schema, executor);

    // ── Accessors ───────────────────────────────────────────────────────────────

    internal List<ThreadMessage> GetChatHistory(int maxMessages = 0, int maxChars = 0)
    {
        List<ThreadMessage> result = new();
        int charCount = 0;

        for (int i = History.Count - 1; i >= 0; i--)
        {
            if (maxMessages > 0 && result.Count >= maxMessages) break;

            ThreadItem item = History[i];
            if (string.IsNullOrEmpty(item.Message)) continue;

            int itemLen = item.AuthorName.Length + 2 + item.Message.Length;
            if (maxChars > 0 && charCount + itemLen > maxChars) break;

            charCount += itemLen;
            result.Add(new ThreadMessage(
                Role:     item.AuthorName == "ARI" ? "assistant" : "user",
                Username: item.AuthorName,
                Content:  item.Message));
        }

        result.Reverse();
        return result;
    }

    internal List<ThreadMessage> SaveContext()
        => GetChatHistory(shortTermMemoryLimit, maxContextTokens > 0 ? maxContextTokens * 2 : 0);

    /// <summary>Returns (estimatedTokensInContext, tokenLimit). 0 limit means unconfigured.</summary>
    internal (int Used, int Limit) GetContextStats()
    {
        List<ThreadMessage> ctx = SaveContext();
        int chars = ctx.Sum(m => (m.Username?.Length ?? 0) + 2 + (m.Content?.Length ?? 0));
        int estimated = chars / 4;
        return (estimated, maxContextTokens);
    }

    /// <summary>
    /// Resets the inactivity countdown while the user is actively typing.
    /// Only acts when the thread is Active; a thread already Inactive/Dormant is not touched.
    /// </summary>
    internal void ResetInactivityTimer()
    {
        if (state != ThreadState.Active) return;
        inactivityTimer?.Dispose();
        inactivityTimer = new Timer(_ =>
        {
            if (state != ThreadState.Active) return;
            state = ThreadState.Inactive;
            BecameInactive?.Invoke();
        }, null, InactivityThreshold, Timeout.InfiniteTimeSpan);
    }

    internal void MarkEngramProcessed()
    {
        state = ThreadState.Dormant;
        Common.Logger.LogInformation("[Thread] ({ThreadKey}) dormant — scheduled for deletion in {Minutes:F1} minutes.", threadKey, DormantDuration.TotalMinutes);
        dormantTimer = new Timer(_ =>
        {
            state = ThreadState.Deleted;
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

    internal void Seed(IReadOnlyList<ThreadMessage> messages)
    {
        foreach (ThreadMessage m in messages)
        {
            if (m.Role == "assistant")
                History.Add(new AriResponse { Content = m.Content, Timestamp = DateTime.MinValue });
            else
                History.Add(new UserMessage { Username = m.Username, Content = m.Content, Timestamp = DateTime.MinValue });
        }
    }

    // ── Attachment management ───────────────────────────────────────────────────

    internal void AddAttachment(Attachment attachment)
    {
        lock (attachments) { attachments.RemoveAll(a => a.Name == attachment.Name); attachments.Add(attachment); }
    }

    internal bool RemoveAttachment(string name)
    {
        lock (attachments) { return attachments.RemoveAll(a => a.Name == name) > 0; }
    }

    internal IReadOnlyList<Attachment> GetAttachments()
    {
        lock (attachments) { return attachments.ToList().AsReadOnly(); }
    }

    internal void AddMessageAttachment(Attachment attachment)
    {
        lock (pendingMessageAtts) { pendingMessageAtts.RemoveAll(a => a.Name == attachment.Name); pendingMessageAtts.Add(attachment); }
    }

    internal bool RemoveMessageAttachment(string name)
    {
        lock (pendingMessageAtts) { return pendingMessageAtts.RemoveAll(a => a.Name == name) > 0; }
    }

    internal IReadOnlyList<Attachment> GetMessageAttachments()
    {
        lock (pendingMessageAtts) { return pendingMessageAtts.ToList().AsReadOnly(); }
    }

    internal void ClearMessageAttachments()
    {
        lock (pendingMessageAtts) { pendingMessageAtts.Clear(); }
    }

    // ── SendPrompt ──────────────────────────────────────────────────────────────

    internal async Task<string> SendPrompt(
        string               prompt,
        string               username              = "user",
        string?              augmentedPrompt       = null,
        string?              recallNotes           = null,
        string?              contextSummary        = null,
        int                  maxTokensOverride     = 0,
        CancellationToken    ct                    = default,
        bool                 userMessagePreadded   = false,
        Func<string, Task>?  onDelta               = null,
        int                  thinkingBudgetOverride = 0)
    {
        await sendLock.WaitAsync(ct);
        try
        {
            return await SendPromptCore(prompt, username, augmentedPrompt, recallNotes, contextSummary, maxTokensOverride, ct, userMessagePreadded, onDelta, thinkingBudgetOverride);
        }
        catch (OperationCanceledException)
        {
            _liveCall = null;
            if (!preserveOnCancel && History.Count > 0 && History[^1] is UserMessage)
                History.RemoveAt(History.Count - 1);
            preserveOnCancel = false;
            throw;
        }
        finally
        {
            _liveCall = null;
            sendLock.Release();
        }
    }

    private async Task<string> SendPromptCore(
        string               prompt,
        string               username,
        string?              augmentedPrompt,
        string?              recallNotes,
        string?              contextSummary,
        int                  maxTokensOverride,
        CancellationToken    ct,
        bool                 userMessagePreadded,
        Func<string, Task>?  onDelta               = null,
        int                  thinkingBudgetOverride = 0)
    {
        LastMessageAt = DateTime.UtcNow;

        if (state != ThreadState.Active)
        {
            inactivityTimer?.Dispose();
            inactivityTimer = null;
            dormantTimer?.Dispose();
            dormantTimer = null;
            state = ThreadState.Active;
        }

        if (ariRepliedAt != DateTime.MinValue)
        {
            int windowSize = shortTermMemoryLimit > 0 ? shortTermMemoryLimit : 25;
            responseSamples.Add(DateTime.UtcNow - ariRepliedAt);
            if (responseSamples.Count > windowSize)
                responseSamples.RemoveAt(0);
            ariRepliedAt = DateTime.MinValue;
        }

        List<Attachment> threadAtts;
        List<Attachment> msgAtts;
        lock (attachments)        { threadAtts = attachments.ToList(); }
        lock (pendingMessageAtts) { msgAtts    = pendingMessageAtts.ToList(); }

        // ── Build message list ──────────────────────────────────────────────────
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

        List<ThreadMessage> history = GetChatHistory(shortTermMemoryLimit, maxContextTokens > 0 ? maxContextTokens * 2 : 0);

        List<ThreadMessage> collapsed = new();
        foreach (ThreadMessage m in history)
        {
            if (collapsed.Count > 0 && collapsed[^1].Role == m.Role)
                collapsed[^1] = collapsed[^1] with { Content = collapsed[^1].Content + "\n" + m.Content };
            else
                collapsed.Add(m);
        }

        if (augmentedPrompt is not null && collapsed.Count > 0)
            collapsed[^1] = collapsed[^1] with { Content = augmentedPrompt };

        List<object> messages = new List<object> { new { role = "system", content = BuildSystemBlock() } };

        for (int i = 0; i < collapsed.Count - 1; i++)
        {
            ThreadMessage m = collapsed[i];
            messages.Add(new { role = m.Role, content = $"{m.Username}: {m.Content}" });
        }

        if (collapsed.Count > 0)
        {
            ThreadMessage current = collapsed[^1];
            messages.Add(BuildCurrentUserMessage($"{current.Username}: {current.Content}", threadAtts, msgAtts));
        }

        if (!agent.QuietLogging && !agent.SuppressPromptLog)
            Common.Logger.LogInformation("[{Agent}] ({Thread}) prompt\n\"{Prompt}\"", agent.Name, threadKey, prompt);

        // ── Call LLM with tool loop ─────────────────────────────────────────────
        int             maxTokens        = maxTokensOverride != 0 ? maxTokensOverride : agent.MaxTokens;
        int             toolCallCount    = 0;
        List<string>    toolResults      = new();
        HashSet<string> calledKeys       = new(StringComparer.OrdinalIgnoreCase);
        StringBuilder   contentBuilder   = new();
        Stopwatch       sw               = Stopwatch.StartNew();
        bool            wasThinking      = false;
        int             completionTokens = 0;
        int             promptTokens     = 0;

        bool hadImages = msgAtts.Any(a => a.IsImage) || threadAtts.Any(a => a.IsImage);

        // Estimate text-only tokens from the messages already built (4 chars ≈ 1 token).
        // Used to isolate image token cost: imageTokens ≈ promptTokens − estimatedTextTokens.
        // Used to isolate image token cost: imageTokens ≈ promptTokens − estimatedTextTokens.
        int estimatedTextPromptTokens = messages
            .Sum(m =>
            {
                if (m is { } obj)
                {
                    // Anonymous object — content is a string for most messages
                    string? content = obj.GetType().GetProperty("content")?.GetValue(obj) as string;
                    return (content?.Length ?? 0) / 4;
                }
                return 0;
            });

        // Update the live call record in place so the LlmService's ConcurrentDictionary reference
        // stays valid. If no external record was injected (e.g. internal agents), create one now.
        if (_liveCall is { } existing)
        {
            existing.EstimatedInputTokens = estimatedTextPromptTokens;
            existing.OutputTokenLimit     = maxTokens;
            existing.HadImages            = hadImages;
        }
        else
        {
            _liveCall = new LiveCallInfo(agent.Name, threadKey, estimatedTextPromptTokens, maxTokens, maxContextTokens, agent.MaxImageTokens, hadImages);
        }

        while (true)
        {
            bool      toolsExhausted = toolCallCount >= MAX_TOOL_CALLS;
            object[]? toolSchemas    = !toolsExhausted && tools.Count > 0
                                        ? tools.Values.Select(t => t.Schema).ToArray()
                                        : null;

            Dictionary<string, object?> body = new()
            {
                ["model"]          = agent.ModelString,
                ["messages"]       = messages,
                ["stream"]         = true,
                ["stream_options"] = new { include_usage = true },
                ["max_tokens"]     = maxTokens,
                ["temperature"]    = 0.7,
                ["top_p"]          = 0.80,
                ["top_k"]          = 20,
                ["repeat_penalty"] = 1.0
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
            if (toolSchemas is not null)
                body["tools"] = toolSchemas;
            if (agent.Slot.HasValue)
                body["id_slot"] = agent.Slot.Value;

            string             json    = JsonSerializer.Serialize(body);
            HttpRequestMessage request = new(HttpMethod.Post, $"{agent.Endpoint}/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new LlmRequestFailedException($"LLM request failed with status: {response.StatusCode}");

            using Stream      stream = await response.Content.ReadAsStreamAsync(ct);
            using StreamReader reader = new(stream);

            Dictionary<int, (string Id, string Name, StringBuilder Args)> pendingCalls = new();
            string? finishReason = null;
            contentBuilder.Clear();

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
                            }

                            if (tc.TryGetProperty("function", out JsonElement funcEl) &&
                                funcEl.TryGetProperty("arguments", out JsonElement argsEl))
                            {
                                string? argsDelta = argsEl.GetString();
                                if (!string.IsNullOrEmpty(argsDelta) && pendingCalls.TryGetValue(index, out (string Id, string Name, StringBuilder Args) call))
                                    call.Args.Append(argsDelta);
                            }
                        }
                        continue;
                    }

                    if (!delta.TryGetProperty("content", out JsonElement contentEl)) continue;
                    string? deltaText = contentEl.GetString();
                    if (!string.IsNullOrEmpty(deltaText))
                    {
                        contentBuilder.Append(deltaText);
                        if (LiveCall is { } lc) lc.EstimatedOutputTokens = contentBuilder.Length / 4;
                        if (onDelta is not null)
                            await onDelta(contentBuilder.ToString());
                    }
                }
            }

            // Fallback: model emitted tool calls as text instead of structured format
            if (pendingCalls.Count == 0 && contentBuilder.Length > 0)
            {
                MatchCollection textCalls = Regex.Matches(
                    contentBuilder.ToString(),
                    @"<tool_call>\s*<function=(\w+)>(.*?)</function>\s*</tool_call>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                if (textCalls.Count > 0)
                {
                    Common.Logger.LogWarning("[{Agent}] ({Thread}) model used text tool call format — parsing fallback.", agent.Name, threadKey);
                    int fakeIndex = 0;
                    foreach (Match m in textCalls)
                    {
                        string name        = m.Groups[1].Value.Trim();
                        string callBody    = m.Groups[2].Value;
                        StringBuilder argsBuilder = new();
                        argsBuilder.Append('{');
                        bool first = true;
                        foreach (Match p in Regex.Matches(callBody, @"<parameter=(\w+)>\s*(.*?)\s*</parameter>", RegexOptions.Singleline))
                        {
                            if (!first) argsBuilder.Append(',');
                            argsBuilder.Append($"\"{p.Groups[1].Value}\":\"{p.Groups[2].Value.Trim()}\"");
                            first = false;
                        }
                        argsBuilder.Append('}');
                        pendingCalls[fakeIndex++] = ($"fallback_{fakeIndex}", name, argsBuilder);
                    }

                    contentBuilder.Clear();
                    finishReason = "tool_calls";
                }
            }

            if (pendingCalls.Count > 0 && finishReason == "tool_calls")
            {
                // If content was streamed before tool calls were detected, reset the client bubble.
                if (onDelta is not null && contentBuilder.Length > 0)
                    await onDelta(string.Empty);

                toolCallCount += pendingCalls.Count;

                messages.Add(new
                {
                    role       = "assistant",
                    content    = (string?)null,
                    tool_calls = pendingCalls
                        .OrderBy(kv => kv.Key)
                        .Select(kv => new
                        {
                            id       = kv.Value.Id,
                            type     = "function",
                            function = new { name = kv.Value.Name, arguments = kv.Value.Args.ToString() }
                        })
                        .ToArray()
                });

                foreach ((string Id, string Name, StringBuilder Args) call in pendingCalls.Values)
                {
                    string callKey = $"{call.Name}:{call.Args}";
                    bool   isNew   = calledKeys.Add(callKey);
                    string result;

                    if (!isNew)
                    {
                        result = "Already retrieved.";
                    }
                    else if (tools.TryGetValue(call.Name, out (object Schema, Func<string, Task<string>> Execute) tool))
                    {
                        result = await tool.Execute(call.Args.ToString());
                        toolResults.Add(result);
                    }
                    else
                    {
                        result = "Tool not found.";
                    }

                    messages.Add(new { role = "tool", tool_call_id = call.Id, content = result });
                }

                continue;
            }

            break;
        }

        sw.Stop();
        string responseText = contentBuilder.ToString();
        if (responseText.StartsWith("ARI: ", StringComparison.OrdinalIgnoreCase))
            responseText = responseText["ARI: ".Length..];
        if (string.IsNullOrWhiteSpace(responseText))
            throw new LlmRequestFailedException("LLM response was empty.");

        double elapsed   = sw.Elapsed.TotalSeconds;
        double tokPerSec = completionTokens > 0 ? completionTokens / elapsed : 0;

        if (!agent.QuietLogging)
        {
            Common.Logger.LogInformation("[{Agent}] ({Thread}) responded in {Seconds}s ({Tokens} tokens, {TokPerSec} t/s)",
                agent.Name, threadKey, elapsed.ToString("F1"), completionTokens, tokPerSec.ToString("F1"));

            int tokenLimit = maxTokens > 0 ? maxTokens : 0;
            if (tokenLimit > 0 && completionTokens >= tokenLimit * 0.8)
                Common.Logger.LogWarning("[{Agent}] ({Thread}) token usage at {Pct}% of limit ({Used}/{Max})",
                    agent.Name, threadKey, (int)(completionTokens * 100.0 / tokenLimit), completionTokens, tokenLimit);

            Common.Logger.LogInformation("[{Agent}] ({Thread}) response\n\"{Response}\"",
                agent.Name, threadKey, responseText);
        }

        // Merge Memory-agent notes (passed in) with any search_memories tool results collected
        // during this response. Both use the same [Title|URL]\ncontent format.
        List<string> noteParts = new();
        if (!string.IsNullOrEmpty(recallNotes)) noteParts.Add(recallNotes.Trim());
        if (toolResults.Count > 0)              noteParts.Add(string.Join("\n\n", toolResults).TrimEnd());
        string? combinedRecallNotes = noteParts.Count > 0 ? string.Join("\n\n", noteParts) : null;

        _liveCall = null;

        History.Add(new AriResponse
        {
            Content                   = responseText,
            Timestamp                 = DateTime.Now,
            ThinkingSeconds           = elapsed,
            RecallNotes               = combinedRecallNotes,
            ContextSummary            = contextSummary,
            CompletionTokens          = completionTokens,
            OutputTokenLimit          = maxTokens > 0 ? maxTokens : 0,
            PromptTokens              = promptTokens,
            ContextTokenLimit         = maxContextTokens,
            HadImageAttachments       = hadImages,
            EstimatedTextPromptTokens = estimatedTextPromptTokens,
            ImageTokenLimit           = agent.MaxImageTokens,
        });
        Updated?.Invoke();

        ariRepliedAt = DateTime.UtcNow;
        inactivityTimer?.Dispose();
        inactivityTimer = new Timer(_ =>
        {
            if (state != ThreadState.Active) return;
            state = ThreadState.Inactive;
            BecameInactive?.Invoke();
        }, null, InactivityThreshold, Timeout.InfiniteTimeSpan);

        ExchangeCompleted?.Invoke(prompt, responseText);

        if (EngramDue())
            BufferFull?.Invoke();

        return responseText;
    }

    // ── Private helpers ─────────────────────────────────────────────────────────

    private string BuildSystemBlock()
    {
        string body = PlatformContext is null ? agent.SystemPrompt : $"{agent.SystemPrompt}\n\n{PlatformContext}";
        return agent.Think ? body : $"{body}\n<|think_off|>";
    }

    private static object BuildCurrentUserMessage(
        string           promptText,
        List<Attachment> threadAtts,
        List<Attachment> msgAtts)
    {
        List<Attachment> threadImages = threadAtts.Where(a => a.IsImage).ToList();
        List<Attachment> threadTexts  = threadAtts.Where(a => !a.IsImage).ToList();
        List<Attachment> msgImages    = msgAtts.Where(a => a.IsImage).ToList();
        List<Attachment> msgTexts     = msgAtts.Where(a => !a.IsImage).ToList();

        bool hasThreadContent = threadImages.Count > 0 || threadTexts.Count > 0;
        bool hasMsgContent    = msgImages.Count > 0 || msgTexts.Count > 0;

        if (!hasThreadContent && !hasMsgContent)
            return new { role = "user", content = promptText };

        const string divider = "-------------------";
        List<object> contentParts = new();

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
            if (threadTexts.Count > 0) sb.AppendLine(divider);
            contentParts.Add(new { type = "text", text = sb.ToString().TrimEnd() });

            foreach (Attachment a in threadImages)
            {
                string dataUrl = $"data:{a.MimeType ?? "image/jpeg"};base64,{a.Content}";
                contentParts.Add(new { type = "image_url", image_url = new { url = dataUrl } });
            }
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
            if (msgTexts.Count > 0) sb.AppendLine(divider);
            contentParts.Add(new { type = "text", text = sb.ToString().TrimEnd() });

            foreach (Attachment a in msgImages)
            {
                string dataUrl = $"data:{a.MimeType ?? "image/jpeg"};base64,{a.Content}";
                contentParts.Add(new { type = "image_url", image_url = new { url = dataUrl } });
            }
        }

        contentParts.Add(new { type = "text", text = promptText });

        return new { role = "user", content = (object)contentParts };
    }

    private bool EngramDue()
    {
        if (shortTermMemoryLimit <= 0) return false;
        if (History.Count < shortTermMemoryLimit) return false;
        if (History.Count == shortTermMemoryLimit) return true;

        int interval = Math.Max(1, shortTermMemoryLimit / 2);
        return History.Count % interval == 0;
    }
}

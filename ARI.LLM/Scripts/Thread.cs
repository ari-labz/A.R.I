using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public enum ThreadState { Active, Inactive, Dormant, Deleted }

public class Thread
{
    private const int     MIN_INACTIVITY_TIMER     = 30;
    private const int     MIN_DELETION_TIMER       = 15;
    private const int     MIN_INACTIVITY_THRESHOLD = 1;
    private const int     MAX_TOOL_CALLS           = 10;
    private const int     DEFAULT_MEMORY_LIMIT     = 25;
    private const int     CHARS_PER_TOKEN          = 4;
    private const double  TEMPERATURE              = 0.7;
    private const double  TOP_P                    = 0.80;
    private const int     TOP_K                    = 20;
    private const double  REPEAT_PENALTY           = 1.0;
    private const double  TOKEN_WARNING_RATIO      = 0.8;
    private const string  ATTACHMENT_DIVIDER       = "-------------------";

    private readonly Agent      agent;
    private readonly string     threadKey;
    private readonly HttpClient httpClient;

    public readonly List<ThreadItem> History = new();

    private readonly Dictionary<string, (object Schema, Func<string, Task<string>> Execute)> tools = new();

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

    internal void SetLiveCall(LiveCallInfo liveCall) => liveCallInfo = liveCall;

    internal void SignalProcessing()
    {
        if (liveCallInfo is null)
            liveCallInfo = new LiveCallInfo(agent.Name, threadKey, 0, agent.MaxTokens, agent.MaxContextTokens, agent.MaxImageTokens);
    }

    // ── Attachments ────────────────────────────────────────────────────────────
    private readonly List<Attachment> attachments        = new();
    private readonly List<Attachment> pendingMessageAtts = new();

    internal string? PlatformContext { get; init; }

    internal event Action? Updated;
    internal event Action? BufferFull;
    internal event Action<string, string>? ExchangeCompleted;
    internal event Action? BecameInactive;
    internal event Action? Deleted;

    // ── Constructor ─────────────────────────────────────────────────────────────

    internal Thread(Agent agent, string threadKey, string? platformContext = null)
    {
        this.agent      = agent;
        this.threadKey  = threadKey;
        httpClient      = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        PlatformContext = platformContext;
    }

    // ── Tools ───────────────────────────────────────────────────────────────────

    internal void RegisterTool(string name, object schema, Func<string, Task<string>> executor)
        => tools[name] = (schema, executor);

    // ── History ─────────────────────────────────────────────────────────────────

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

    internal List<ThreadMessage> ContextSnapshot()
    {
        int maxChars = agent.MaxContextTokens > 0 ? agent.MaxContextTokens * 2 : 0;
        return GetChatHistory(agent.MemoryLimit, maxChars);
    }

    public (int Used, int Limit) GetContextStats()
    {
        List<ThreadMessage> ctx = ContextSnapshot();
        int chars               = ctx.Sum(m => (m.Username?.Length ?? 0) + 2 + (m.Content?.Length ?? 0));
        return (chars / CHARS_PER_TOKEN, agent.MaxContextTokens);
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
            return await Send(prompt, username, augmentedPrompt, recallNotes, contextSummary, maxTokensOverride, ct, userMessagePreadded, onDelta, thinkingBudgetOverride);
        }
        catch (OperationCanceledException)
        {
            liveCallInfo = null;
            if (!preserveOnCancel && History.Count > 0 && History[^1] is UserMessage)
                History.RemoveAt(History.Count - 1);
            preserveOnCancel = false;
            throw;
        }
        finally
        {
            liveCallInfo = null;
            sendLock.Release();
        }
    }

    private async Task<string> Send(
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

        if (State != ThreadState.Active)
        {
            inactivityTimer?.Dispose();
            inactivityTimer = null;
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

        string systemContent = PlatformContext is null
            ? agent.SystemPrompt
            : $"{agent.SystemPrompt}\n\n{PlatformContext}";
        if (!agent.Think)
            systemContent = $"{systemContent}\n<|think_off|>";

        List<object> messages = new List<object> { new { role = "system", content = systemContent } };

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
                    if (threadTexts.Count > 0) sb.AppendLine(ATTACHMENT_DIVIDER);
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
                    if (msgTexts.Count > 0) sb.AppendLine(ATTACHMENT_DIVIDER);
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
        List<string>    toolResults      = new();
        HashSet<string> calledKeys       = new(StringComparer.OrdinalIgnoreCase);
        StringBuilder   responseBuilder  = new();
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
                ["temperature"]    = TEMPERATURE,
                ["top_p"]          = TOP_P,
                ["top_k"]          = TOP_K,
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
                throw new LlmRequestFailedException($"LLM request failed with status: {response.StatusCode}");

            using Stream      stream = await response.Content.ReadAsStreamAsync(ct);
            using StreamReader reader = new(stream);

            Dictionary<int, (string Id, string Name, StringBuilder Args)> pendingCalls = new();
            string? finishReason = null;
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
                        responseBuilder.Append(deltaText);
                        if (LiveCall is { } lc) lc.EstimatedOutputTokens = responseBuilder.Length / CHARS_PER_TOKEN;
                        if (onDelta is not null)
                            await onDelta(responseBuilder.ToString());
                    }
                }
            }

            if (pendingCalls.Count == 0 && responseBuilder.Length > 0)
            {
                MatchCollection textCalls = Regex.Matches(
                    responseBuilder.ToString(),
                    @"<tool_call>\s*<function=(\w+)>(.*?)</function>\s*</tool_call>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                if (textCalls.Count > 0)
                {
                    Common.Logger.LogWarning("[{Agent}] ({Thread}) model used text tool call format — parsing fallback.", agent.Name, threadKey);
                    int fakeIndex = 0;
                    foreach (Match m in textCalls)
                    {
                        string        name        = m.Groups[1].Value.Trim();
                        string        callBody    = m.Groups[2].Value;
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

                    responseBuilder.Clear();
                    finishReason = "tool_calls";
                }
            }

            if (pendingCalls.Count > 0 && finishReason == "tool_calls")
            {
                if (onDelta is not null && responseBuilder.Length > 0)
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
                        result = "Already retrieved.";
                    else if (tools.TryGetValue(call.Name, out (object Schema, Func<string, Task<string>> Execute) tool))
                    {
                        result = await tool.Execute(call.Args.ToString());
                        toolResults.Add(result);
                    }
                    else
                        result = "Tool not found.";

                    messages.Add(new { role = "tool", tool_call_id = call.Id, content = result });
                }

                continue;
            }

            break;
        }

        sw.Stop();
        string responseText = responseBuilder.ToString();
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

            if (maxTokens > 0 && completionTokens >= maxTokens * TOKEN_WARNING_RATIO)
                Common.Logger.LogWarning("[{Agent}] ({Thread}) token usage at {Pct}% of limit ({Used}/{Max})",
                    agent.Name, threadKey, (int)(completionTokens * 100.0 / maxTokens), completionTokens, maxTokens);

            Common.Logger.LogInformation("[{Agent}] ({Thread}) response\n\"{Response}\"",
                agent.Name, threadKey, responseText);
        }

        List<string> noteParts = new();
        if (!string.IsNullOrEmpty(recallNotes)) noteParts.Add(recallNotes.Trim());
        if (toolResults.Count > 0)              noteParts.Add(string.Join("\n\n", toolResults).TrimEnd());
        string? combinedNotes = noteParts.Count > 0 ? string.Join("\n\n", noteParts) : null;

        liveCallInfo = null;

        History.Add(new AriResponse
        {
            Content                   = responseText,
            Timestamp                 = DateTime.Now,
            ThinkingSeconds           = elapsed,
            RecallNotes               = combinedNotes,
            ContextSummary            = contextSummary,
            CompletionTokens          = completionTokens,
            OutputTokenLimit          = maxTokens > 0 ? maxTokens : 0,
            PromptTokens              = promptTokens,
            ContextTokenLimit         = agent.MaxContextTokens,
            HadImageAttachments       = hadImages,
            EstimatedTextPromptTokens = estimatedTextTokens,
            ImageTokenLimit           = agent.MaxImageTokens,
        });
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
}

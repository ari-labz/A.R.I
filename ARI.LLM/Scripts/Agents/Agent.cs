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
    [JsonPropertyName("slot")]          public int?    Slot          { get; set; }
    [JsonPropertyName("temperature")]   public double? Temperature   { get; init; }
    [JsonPropertyName("topP")]          public double? TopP          { get; init; }
    [JsonPropertyName("topK")]          public int?    TopK          { get; init; }
    [JsonPropertyName("repeatPenalty")] public double? RepeatPenalty { get; init; }
    [JsonPropertyName("presencePenalty")]  public double? PresencePenalty  { get; init; }
    [JsonPropertyName("frequencyPenalty")]  public double? FrequencyPenalty  { get; init; }
    [JsonPropertyName("maxContextTokens")] public int     MaxContextTokens  { get; init; }

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
    private const double COMPACT_RATIO       = 0.6;
    private const int    COMPACT_KEEP_RECENT = 3;
    private const int    MAX_DEGRADE_EVENTS  = 5;
    private const int    DEFAULT_MEMORY_LIMIT = 25;
    private const string ATTACHMENT_DIVIDER  = "-------------------";

    private readonly HttpClient httpClient = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    // ── Context-building hooks (overridden by specialised agents) ────────────
    internal virtual string BuildPersistentContext(Thread thread)    => "";
    internal virtual string RenderDynamicContextBlock(Thread thread) => "";
    internal virtual int    IncompleteTasks(Thread thread)           => 0;
    internal virtual bool   HasTasks(Thread thread)                  => false;
    internal virtual string PendingTaskSummary(Thread thread)        => "";

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
        int                 thinkingBudgetOverride = 0)
    {
        await thread.sendLock.WaitAsync(ct);
        try
        {
            return await Send(thread, prompt, username, augmentedPrompt, recallNotes, contextSummary, maxTokensOverride, ct, userMessagePreadded, onDelta, thinkingBudgetOverride);
        }
        catch (OperationCanceledException)
        {
            thread.liveCallInfo = null;
            if (!thread.preserveOnCancel)
            {
                if (thread.streamingResponse is not null) thread.History.Remove(thread.streamingResponse);
                if (thread.History.Count > 0 && thread.History[^1] is UserMessage) thread.History.RemoveAt(thread.History.Count - 1);
            }
            else if (thread.streamingResponse is not null)
            {
                thread.streamingResponse.Content = AriContentBlock.Parse(thread.streamedText);
                thread.streamingResponse.State   = AriResponseState.Cancelled;
            }
            thread.preserveOnCancel  = false;
            thread.streamingResponse = null;
            throw;
        }
        catch (Exception ex)
        {
            if (thread.streamingResponse is not null)
            {
                thread.streamingResponse.Content = AriContentBlock.Parse(
                    string.IsNullOrWhiteSpace(thread.streamedText)
                        ? $"[Error: {ex.Message}]"
                        : thread.streamedText);
                thread.streamingResponse.State = AriResponseState.Error;
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
        int                 thinkingBudgetOverride = 0)
    {
        thread.LastMessageAt = DateTime.UtcNow;

        thread.inactivityTimer?.Dispose();
        thread.inactivityTimer = null;

        if (thread.State != ThreadState.Active)
        {
            thread.dormantTimer?.Dispose();
            thread.dormantTimer = null;
            thread.State = ThreadState.Active;
        }

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
            UserMessage? lastMsg = thread.History.OfType<UserMessage>().LastOrDefault();
            msgAtts = lastMsg?.Attachments?.ToList() ?? new();
        }
        else
        {
            msgAtts = thread.SnapshotMessageAttachments(fromHistory: false);
        }

        if (!userMessagePreadded)
        {
            thread.History.Add(new UserMessage
            {
                Username    = username,
                Content     = prompt,
                Timestamp   = DateTime.Now,
                Attachments = msgAtts.Count > 0 ? msgAtts.ToList() : null
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
        string thinkSuffix = Think ? "" : "\n<|think_off|>";

        List<object> messages = new List<object> { new { role = "system", content = baseSystem + thinkSuffix } };

        for (int i = 0; i < collapsed.Count - 1; i++)
        {
            ThreadMessage m = collapsed[i];
            messages.Add(new { role = m.Role, content = $"{m.Username}: {m.Content}" });
        }

        if (collapsed.Count > 0)
        {
            ThreadMessage current   = collapsed[^1];
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
        Dictionary<string, int> readCounts   = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string>         editedFiles  = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string>         earlyEditAbortedOnce = new(StringComparer.OrdinalIgnoreCase);
        int                     buildState   = 0;
        int                     continueNudges = 0;
        Dictionary<string, int> editFailStreak = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> writeCounts  = new(StringComparer.OrdinalIgnoreCase);
        bool                    forceNoMoreTools = false;
        Dictionary<string, (string Result, int Count)> commandCache = new(StringComparer.Ordinal);
        HashSet<string>         editedPathsThisBatch = new(StringComparer.OrdinalIgnoreCase);
        int                     todoReminders = 0;
        HashSet<string>         turnEditPaths = new(StringComparer.OrdinalIgnoreCase);
        bool                    todoNudged = false;
        static string NormKey(string p) => System.IO.Path.GetFileName(p.Trim('"', '\'', ' ', '\\'));
        static bool IsBuildCmd(string c) => System.Text.RegularExpressions.Regex.IsMatch(c,
            @"(?i)\b(dotnet\s+(build|publish|msbuild)|msbuild|make|cargo\s+build|go\s+build|npm\s+run\s+build|yarn\s+build|tsc)\b");
        static bool IsTestCmd(string c) => System.Text.RegularExpressions.Regex.IsMatch(c,
            @"(?i)\b(dotnet\s+(test|vstest)|vstest|cargo\s+test|go\s+test|pytest|npm\s+(run\s+)?test|yarn\s+test|jest)\b");
        static string? CondenseBuildErrors(string output)
        {
            System.Text.RegularExpressions.MatchCollection ms = System.Text.RegularExpressions.Regex.Matches(
                output, @"(?im)^.*?:\s*error\s+[A-Za-z]+\d+:.*$");
            if (ms.Count == 0) return null;
            List<string> seen = new();
            foreach (System.Text.RegularExpressions.Match m in ms)
            {
                string line = m.Value.Trim();
                string key  = System.Text.RegularExpressions.Regex.Replace(line, @"\s*\[[^\]]*\]\s*$", "");
                if (!seen.Contains(key)) seen.Add(key);
            }
            int total = seen.Count;
            StringBuilder sb = new();
            sb.AppendLine($"Build failed with {total} error{(total == 1 ? "" : "s")}{(total > 10 ? " (showing the first 10)" : "")}:");
            foreach (string e in seen.Take(10)) sb.AppendLine(e);
            if (total > 10) sb.AppendLine($"... and {total - 10} more error(s). Fix the errors above (they list the file and line), then rebuild.");
            else            sb.AppendLine("Fix the errors above (they list the file and line), then rebuild.");
            return sb.ToString().TrimEnd();
        }
        List<(int Index, string CallId, string Name)> toolResultSlots = new();
        int degradeEvents = 0;
        void Degrade()
        {
            if (++degradeEvents >= MAX_DEGRADE_EVENTS)
                throw new LlmRequestFailedException(
                    $"Tool-call formatting failed {degradeEvents} times this turn — stopping to avoid a spiral. Any changes already applied are kept.");
        }
        Dictionary<string, (int Index, string CallId)> liveReads = new(StringComparer.OrdinalIgnoreCase);
        StringBuilder   responseBuilder  = new();
        StringBuilder   contentBuilder   = new();
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

        AriResponse ariResponse = new() { Timestamp = DateTime.Now };
        thread.History.Add(ariResponse);
        thread.streamingResponse = ariResponse;
        thread.streamedText      = "";
        Func<string, Task>? userDelta = onDelta;
        onDelta = async text => { thread.streamedText = text; if (userDelta is not null) await userDelta(text); };

        while (true)
        {
            messages[0] = new { role = "system", content = baseSystem + RenderDynamicContextBlock(thread) + thinkSuffix };

            CompactToolOutput(messages, toolResultSlots, MaxContextTokens);

            if (thread.liveCallInfo is { } lci)
            {
                long totalChars = messages.Sum(m => (long)(ContentOf(m)?.Length ?? 0));
                lci.EstimatedInputTokens = (int)(totalChars / CHARS_PER_TOKEN);
            }

            bool      toolsExhausted = forceNoMoreTools || (MaxToolCalls > 0 && toolCallCount >= MaxToolCalls);
            object[]? toolSchemas    = !toolsExhausted && thread.tools.Count > 0
                                        ? thread.tools.Values.Select(t => t.Schema).ToArray()
                                        : null;

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

            if (!Think)
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

            if (toolSchemas is not null) body["tools"]   = toolSchemas;
            if (Slot.HasValue)           body["id_slot"] = Slot.Value;

            string             json    = JsonSerializer.Serialize(body);
            HttpRequestMessage request = new(HttpMethod.Post, $"{Endpoint}/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

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
                            if (!Think)
                                Shared.Logger.LogWarning("[{Agent}] ({Thread}) thinking chain detected — <|think_off|> may not be working.", Name, thread.Key);
                            wasThinking = true;
                        }
                    }

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

                                    if (earlyAbort is null && call.Name == "edit_file" && !precheckedCalls.Contains(index))
                                    {
                                        string? editPath = ToolCallParser.TryExtractJsonString(call.Args.ToString(), "path");
                                        if (editPath is not null)
                                        {
                                            precheckedCalls.Add(index);
                                            string ekey = NormKey(editPath);
                                            bool readThisTurn = readCounts.ContainsKey(ekey)
                                                || editedFiles.Contains(ekey)
                                                || threadAtts.Any(a => NormKey(a.Name) == ekey)
                                                || msgAtts.Any(a => NormKey(a.Name) == ekey);
                                            bool readInBatch = pendingCalls.Values.Any(pc =>
                                                (pc.Name == "read_file" || pc.Name == "preview_file")
                                                && ToolCallParser.TryExtractJsonString(pc.Args.ToString(), "path") is { } rp
                                                && NormKey(rp) == ekey);
                                            if (!readThisTurn && !readInBatch && !earlyEditAbortedOnce.Contains(ekey))
                                            {
                                                earlyEditAbortedOnce.Add(ekey);
                                                earlyAbort = (call.Id, call.Name, call.Args.ToString(),
                                                    $"[System: Aborted before the edit completed — you have not read {editPath} this turn, so any old_string would be guessed and the edit would fail. Call preview_file then read_file (with start_line/end_line) on {editPath} first, then edit it.]");
                                                Shared.Logger.LogWarning("[{Agent}] ({Thread}) Streaming abort: edit_file on unread file '{File}' — generation cancelled mid-stream.", Name, thread.Key, editPath);
                                                break;
                                            }
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
                        if (earlyAbort is not null) break;
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
                            await onDelta(contentBuilder.ToString() + visible);
                        }
                    }
                }
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

                List<ToolCallParser.Call>? textCalls = ToolCallParser.ParseTextCalls(responseBuilder.ToString());
                if (textCalls is not null)
                {
                    consecutiveFallbacks++;
                    Degrade();
                    if (consecutiveFallbacks > 3)
                        throw new LlmRequestFailedException($"Model stuck in text tool call fallback loop ({consecutiveFallbacks} consecutive) — aborting.");
                    Shared.Logger.LogWarning("[{Agent}] ({Thread}) model used text tool call format — parsing fallback.", Name, thread.Key);
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
                foreach (var key in pendingCalls.Keys)
                {
                    var (id, name, args) = pendingCalls[key];
                    string raw      = args.ToString();
                    string stripped = ToolCallParser.StripThinkLeaks(raw);
                    string repaired = ToolCallParser.RepairArgs(stripped);

                    if (stripped != raw)
                        Shared.Logger.LogWarning("[{Agent}] ({Thread}) Stripped <think> leakage from args for tool '{Tool}'.", Name, thread.Key, name);
                    if (repaired != stripped)
                        Shared.Logger.LogWarning("[{Agent}] ({Thread}) Repaired malformed JSON args for tool '{Tool}'.", Name, thread.Key, name);

                    if (repaired != raw)
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

                editedPathsThisBatch.Clear();

                HashSet<string> readOnlyTools = new(StringComparer.OrdinalIgnoreCase)
                    { "read_file", "search_files", "list_directory", "find_files" };
                Dictionary<int, Task<string>> prelaunched = new();
                if (pendingCalls.Count > 1)
                    foreach (var (idx, c) in pendingCalls)
                        if (readOnlyTools.Contains(c.Name) && thread.tools.TryGetValue(c.Name, out var roTool))
                            prelaunched[idx] = roTool.Execute(c.Args.ToString());

                foreach (var (callIndex, call) in pendingCalls)
                {
                    string result;

                    if (call.Name is "preview_file")
                    {
                        try
                        {
                            using JsonDocument pdoc = JsonDocument.Parse(call.Args.ToString());
                            string ppath = NormKey(pdoc.RootElement.GetProperty("path").GetString() ?? "");
                            if (commandCache.TryGetValue($"preview_file:{ppath}", out var cachedPreview))
                            {
                                commandCache[$"preview_file:{ppath}"] = (cachedPreview.Result, cachedPreview.Count + 1);
                                string previewNudge = cachedPreview.Count >= 2
                                    ? $"[System: You have previewed {ppath} {cachedPreview.Count + 1} times this turn. Stop previewing it — use read_file with start_line/end_line to read the section you need.]"
                                    : $"[System: You already previewed {ppath} this turn. Here is the cached outline — do not preview it again:\n\n{cachedPreview.Result}]";
                                if (isXmlFallback) { xmlResultsMsg!.AppendLine($"--- {call.Name} ---"); xmlResultsMsg.AppendLine(previewNudge); xmlResultsMsg.AppendLine(); }
                                else { messages.Add(new { role = "tool", tool_call_id = call.Id, name = call.Name, content = previewNudge }); if (thread.liveCallInfo is { } lc2) lc2.EstimatedInputTokens += previewNudge.Length / CHARS_PER_TOKEN; }
                                continue;
                            }
                        }
                        catch { /* ignore */ }
                    }

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
                                    if (thread.liveCallInfo is { } lc) lc.EstimatedInputTokens += result.Length / CHARS_PER_TOKEN;
                                }
                                continue;
                            }
                        }
                        catch { /* ignore */ }
                    }

                    if (call.Name == "edit_file")
                    {
                        string? editPath = null;
                        try
                        {
                            using JsonDocument edoc = JsonDocument.Parse(call.Args.ToString());
                            editPath = (edoc.RootElement.GetProperty("path").GetString() ?? "").Trim('"', '\'', ' ', '\\');
                        }
                        catch { /* fall through */ }

                        if (!string.IsNullOrEmpty(editPath))
                        {
                            string ekey = NormKey(editPath);

                            if (editedPathsThisBatch.Contains(ekey))
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
                                    if (thread.liveCallInfo is { } lc) lc.EstimatedInputTokens += result.Length / CHARS_PER_TOKEN;
                                }
                                continue;
                            }
                            editedPathsThisBatch.Add(ekey);
                        }
                    }

                    if (!todoNudged && !HasTasks(thread) && call.Name is "edit_file" or "write_file")
                    {
                        string? tp = null;
                        try
                        {
                            using JsonDocument tdoc = JsonDocument.Parse(call.Args.ToString());
                            tp = NormKey(tdoc.RootElement.GetProperty("path").GetString() ?? "");
                        }
                        catch { /* skip the nudge */ }

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
                                if (thread.liveCallInfo is { } lc) lc.EstimatedInputTokens += result.Length / CHARS_PER_TOKEN;
                            }
                            continue;
                        }
                        if (!string.IsNullOrEmpty(tp)) turnEditPaths.Add(tp);
                    }
                    else if (call.Name is "edit_file" or "write_file")
                    {
                        try
                        {
                            using JsonDocument tdoc = JsonDocument.Parse(call.Args.ToString());
                            string tp = NormKey(tdoc.RootElement.GetProperty("path").GetString() ?? "");
                            if (!string.IsNullOrEmpty(tp)) turnEditPaths.Add(tp);
                        }
                        catch { /* ignore */ }
                    }

                    if (thread.tools.TryGetValue(call.Name, out var tool))
                    {
                        if (call.Name == "run_command" && buildState != 1)
                        {
                            string cmdLine = ToolCallParser.TryExtractJsonString(call.Args.ToString(), "command") ?? "";
                            if (IsTestCmd(cmdLine) && !IsBuildCmd(cmdLine))
                            {
                                result = buildState == 2
                                    ? "[System: The build is currently failing — do not run tests yet. Fix the build errors first (run the build, resolve every reported error), then run the tests once it builds cleanly.]"
                                    : "[System: Build before you test. Run the build first (e.g. 'dotnet build' on the project you changed) and confirm it reports no errors; only run tests if the build succeeds, otherwise you are testing stale binaries.]";
                                Shared.Logger.LogInformation("[{Agent}] ({Thread}) blocked test before {State} build: {Cmd}", Name, thread.Key, buildState == 2 ? "failed" : "successful", cmdLine);
                                contentBuilder.Append($"<!--ari-tool-error:run_command::{ToolCallParser.EscapeLabel(result)}-->");
                                if (onDelta is not null) await onDelta(contentBuilder.ToString());
                                if (isXmlFallback) { xmlResultsMsg!.AppendLine($"--- {call.Name} ---"); xmlResultsMsg.AppendLine(result); xmlResultsMsg.AppendLine(); }
                                else { messages.Add(new { role = "tool", tool_call_id = call.Id, name = call.Name, content = result }); if (thread.liveCallInfo is { } lcBT) lcBT.EstimatedInputTokens += result.Length / CHARS_PER_TOKEN; }
                                continue;
                            }
                        }

                        if (tool.Display is not null)
                        {
                            string finalMarker = tool.Display(call.Args.ToString());
                            if (streamingMarkers.TryGetValue(callIndex, out string? prevStreamMarker))
                                ReplaceInBuilder(contentBuilder, prevStreamMarker, finalMarker);
                            else
                                contentBuilder.Append(finalMarker);
                            if (onDelta is not null) await onDelta(contentBuilder.ToString());
                        }

                        if (call.Name == "run_command")
                        {
                            string cmdKey = call.Args.ToString().Trim();
                            if (commandCache.TryGetValue(cmdKey, out var cached))
                            {
                                commandCache[cmdKey] = (cached.Result, cached.Count + 1);
                                result = cached.Count >= 2
                                    ? $"[System: You have run this exact command {cached.Count + 1} times this turn. Do not call it again — you already have the output. Use what you know to proceed or respond to the user.]"
                                    : $"[System: You already ran this command earlier this turn. Here is the cached output — do not call it again:\n\n{cached.Result}]";
                                goto AfterToolExecute;
                            }
                        }

                        result = prelaunched.TryGetValue(callIndex, out Task<string>? pre)
                            ? await pre
                            : await tool.Execute(call.Args.ToString());

                        if (call.Name == "run_command")
                        {
                            string cmdStr     = call.Args.ToString().Trim();
                            string cmdTrimmed = cmdStr.Trim('"', '\'', ' ');
                            if (System.Text.RegularExpressions.Regex.IsMatch(cmdTrimmed, @"^\S+\.(csproj|sln|cs|fs|vb|py|ts|tsx|js|jsx|json|xml|yaml|yml|sh|ps1)$"))
                                result = $"[System: \"{cmdTrimmed}\" is a filename, not a shell command — nothing was executed. Did you mean 'dotnet build {cmdTrimmed}', 'dotnet run --project {cmdTrimmed}', or similar?]";
                            else
                                commandCache[cmdStr] = (result, 1);

                            string cmdLine = ToolCallParser.TryExtractJsonString(call.Args.ToString(), "command") ?? "";
                            if (IsBuildCmd(cmdLine) || IsTestCmd(cmdLine))
                            {
                                bool failed = result.Contains("Build FAILED")
                                    || result.Contains(": error ")
                                    || System.Text.RegularExpressions.Regex.IsMatch(result, @"\b[1-9]\d*\s+Error\(s\)");
                                bool ok = !failed && (result.Contains("Build succeeded") || result.Contains("0 Error(s)"));
                                if (ok)          buildState = 1;
                                else if (failed) buildState = 2;

                                if (buildState == 2 && CondenseBuildErrors(result) is { } condensed)
                                    result = condensed;
                            }
                        }
                        if (call.Name == "preview_file")
                        {
                            try
                            {
                                using JsonDocument pd2 = JsonDocument.Parse(call.Args.ToString());
                                string pp = NormKey(pd2.RootElement.GetProperty("path").GetString() ?? "");
                                commandCache[$"preview_file:{pp}"] = (result, 1);
                            }
                            catch { /* ignore */ }
                        }

                        AfterToolExecute:
                        if (call.Name == "edit_file")
                        {
                            try
                            {
                                using JsonDocument argDoc = JsonDocument.Parse(call.Args.ToString());
                                string editPath = (argDoc.RootElement.GetProperty("path").GetString() ?? "").Trim('"', '\'', ' ');
                                string editKey  = NormKey(editPath);
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
                                    if (streak >= 2)
                                        result += " Stop retyping the text. You have the line numbers from read_file/search_files — change these lines with start_line/end_line instead of old_string (one edit_file call with an 'edits' array if several lines), or rewrite the whole file with write_file. If you still cannot, stop and tell the user what is blocking you.";
                                }
                            }
                            catch { /* ignore */ }
                        }

                        if (call.Name == "write_file" && result.Contains("Successfully wrote"))
                        {
                            try
                            {
                                using JsonDocument argDoc = JsonDocument.Parse(call.Args.ToString());
                                string writePath = NormKey(argDoc.RootElement.GetProperty("path").GetString() ?? "");
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
                                    Shared.Logger.LogWarning("[{Agent}] ({Thread}) write_file called {Count}x on '{File}' — cutting off tools for this turn.", Name, thread.Key, wc, writePath);
                                }
                            }
                            catch { /* ignore */ }
                        }

                        if (ToolCallParser.IsError(result))
                        {
                            Shared.Logger.LogError("[{Agent}] ({Thread}) Tool '{Tool}' failed: {Error}", Name, thread.Key, call.Name, result);
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
                        Shared.Logger.LogError("[{Agent}] ({Thread}) Model called unknown tool '{Tool}'", Name, thread.Key, call.Name);
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
                        if (thread.liveCallInfo is { } lc) lc.EstimatedInputTokens += result.Length / CHARS_PER_TOKEN;

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
                        catch { /* ignore */ }
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

            bool toolsStillAvailable = !forceNoMoreTools && !(MaxToolCalls > 0 && toolCallCount >= MaxToolCalls);
            if (pendingCalls.Count == 0 && IncompleteTasks(thread) > 0 && todoReminders < 1 && toolsStillAvailable)
            {
                todoReminders++;
                string pending = PendingTaskSummary(thread);
                Shared.Logger.LogInformation("[{Agent}] ({Thread}) finish-time checklist reminder ({Count} incomplete).", Name, thread.Key, IncompleteTasks(thread));
                messages.Add(new { role = "user", content =
                    $"[System: You still have incomplete checklist items:\n{pending}\n" +
                    "Complete them now (make the changes, then call update_todos to mark them completed), " +
                    "or call update_todos to remove any that are no longer needed. Do not finish until the checklist is resolved.]" });
                responseBuilder.Clear();
                continue;
            }

            if (pendingCalls.Count == 0 && toolsStillAvailable && continueNudges < 2)
            {
                string tail = responseBuilder.ToString().TrimEnd();
                bool promisesAction = tail.Length > 0 && (
                    tail.EndsWith(":")
                    || System.Text.RegularExpressions.Regex.IsMatch(tail,
                        @"(?i)\b(let me|let's|i'll|i will|i'm going to|i need to|now i'll|first,? i|next,? i)\b[^.!?]{0,100}$"));
                bool mentionsVerb = System.Text.RegularExpressions.Regex.IsMatch(tail,
                    @"(?i)\b(read|check|run|build|test|look|examine|open|search|edit|create|add|update|fix|verify|inspect|modify|write|review|rebuild|re-?run)\b");
                if (promisesAction && mentionsVerb)
                {
                    continueNudges++;
                    Shared.Logger.LogInformation("[{Agent}] ({Thread}) premature-stop nudge — model announced an action without performing it.", Name, thread.Key);
                    messages.Add(new { role = "user", content =
                        "[System: You described the next action but did not perform it — no tool call was made. If more work remains, issue the tool call now and keep going until the task is done (build first, then run tests only if the build succeeds). If you are genuinely finished, give your final summary to the user instead.]" });
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

        double elapsed   = sw.Elapsed.TotalSeconds;
        double tokPerSec = completionTokens > 0 ? completionTokens / elapsed : 0;

        if (!QuietLogging)
        {
            Shared.Logger.LogInformation("[{Agent}] ({Thread}) responded in {Seconds}s ({Tokens} tokens, {TokPerSec} t/s)",
                Name, thread.Key, elapsed.ToString("F1"), completionTokens, tokPerSec.ToString("F1"));

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

        ariResponse.Content                   = AriContentBlock.Parse(responseText);
        ariResponse.ThinkingSeconds           = elapsed;
        ariResponse.RecallNotes               = combinedNotes;
        ariResponse.ContextSummary            = contextSummary;
        ariResponse.CompletionTokens          = completionTokens;
        ariResponse.OutputTokenLimit          = maxTokens > 0 ? maxTokens : 0;
        ariResponse.PromptTokens              = promptTokens;
        ariResponse.ContextTokenLimit         = MaxContextTokens;
        ariResponse.HadImageAttachments       = hadImages;
        ariResponse.EstimatedTextPromptTokens = estimatedTextTokens;
        ariResponse.ImageTokenLimit           = 0;
        ariResponse.State                     = AriResponseState.Complete;
        thread.streamingResponse              = null;
        thread.RaiseUpdated();

        thread.ariRepliedAt = DateTime.UtcNow;
        thread.inactivityTimer?.Dispose();
        thread.inactivityTimer = new Timer(_ =>
        {
            if (thread.State != ThreadState.Active) return;
            thread.State = ThreadState.Inactive;
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

    private static string ExtractLogText(string content) =>
        string.Concat(AriContentBlock.Parse(content).OfType<TextBlock>().Select(b => b.Text))
            .Replace("<!--ari-batch-end-->", "")
            .Trim();

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

    private static string? ContentOf(object m) => m.GetType().GetProperty("content")?.GetValue(m) as string;

    private static void CompactToolOutput(List<object> messages, List<(int Index, string CallId, string Name)> slots, int maxContextTokens)
    {
        if (maxContextTokens <= 0) return;

        long budget = (long)(maxContextTokens * (long)CHARS_PER_TOKEN * COMPACT_RATIO);
        long total  = 0;
        foreach (object m in messages) total += ContentOf(m)?.Length ?? 0;

        int stubbable = slots.Count - COMPACT_KEEP_RECENT;
        for (int i = 0; i < stubbable; i++)
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

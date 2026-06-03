using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Thread
{
    private readonly Agent      agent;
    private readonly string     threadKey;
    private readonly HttpClient httpClient;

    // ── Display mode (regular dialogue threads) ────────────────────────────────
    private readonly List<ThreadItem>? threadHistory;
    private int  conversationTurnCount;
    private bool bufferEverFilled;

    // ── AdHoc mode (Engram write threads — LLM-only, no display history) ──────
    private readonly List<ChatMessage>? seedMessages;

    // ── Attachments ────────────────────────────────────────────────────────────
    private readonly List<Attachment> attachments        = new();
    private readonly List<Attachment> pendingMessageAtts = new();

    internal string? ContextPrompt { get; init; }

    internal DateTime LastMessageAt { get; private set; } = DateTime.MinValue;

    /// <summary>Fires whenever a ThreadItem is added to this thread's history.</summary>
    internal event Action? HistoryUpdated;

    /// <summary>Fires when the conversation reaches the agent's memory limit.</summary>
    internal event Action? BufferFull;
    internal event Action<string, string>? ExchangeCompleted;

    // ── Constructors ────────────────────────────────────────────────────────────

    /// <summary>Regular display thread — used by Dialogue for user-facing conversations.</summary>
    internal Thread(Agent agent, string threadKey, string? contextPrompt = null)
    {
        this.agent     = agent;
        this.threadKey = threadKey;
        httpClient     = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        threadHistory  = new List<ThreadItem>();
        ContextPrompt  = contextPrompt;
    }

    /// <summary>
    /// AdHoc thread — seeded from a context snapshot, LLM-only.
    /// Used by Engram's per-note write phase so each note write starts from the same context.
    /// </summary>
    internal Thread(Agent agent, string threadKey, IReadOnlyList<ChatMessage> seedMessages)
    {
        this.agent        = agent;
        this.threadKey    = threadKey;
        httpClient        = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        this.seedMessages = seedMessages.ToList();
    }

    // ── Accessors ───────────────────────────────────────────────────────────────

    internal IReadOnlyList<ThreadItem> GetThreadHistory()
        => (IReadOnlyList<ThreadItem>?)threadHistory?.AsReadOnly() ?? Array.Empty<ThreadItem>();

    /// <summary>
    /// Derives the LLM context window from thread history.
    /// Walks backwards, collects items with a non-null Message, stops at maxMessages or maxChars.
    /// Returns the list in chronological order (oldest first).
    /// </summary>
    internal List<ThreadMessage> GetChatHistory(int maxMessages, int maxChars)
    {
        if (threadHistory is null) return new List<ThreadMessage>();

        var result    = new List<ThreadMessage>();
        int charCount = 0;

        for (int i = threadHistory.Count - 1; i >= 0; i--)
        {
            if (result.Count >= maxMessages) break;

            ThreadItem item = threadHistory[i];
            if (string.IsNullOrEmpty(item.Message)) continue;

            string formatted = $"{item.AuthorName}: {item.Message}";
            if (charCount + formatted.Length > maxChars) break;

            charCount += formatted.Length;
            result.Add(new ThreadMessage(
                Role:     item.AuthorName == "ARI" ? "assistant" : "user",
                Username: item.AuthorName,
                Content:  formatted));
        }

        result.Reverse();
        return result;
    }

    /// <summary>
    /// Returns a snapshot of the current LLM context as ChatMessages for seeding Engram ad-hoc threads.
    /// Includes the system prompt as the first entry.
    /// </summary>
    internal List<ChatMessage> GetSnapshotForAdHoc()
    {
        if (threadHistory is null) return seedMessages?.ToList() ?? new List<ChatMessage>();

        int maxChars = agent.MaxContextTokens > 0 ? agent.MaxContextTokens * 2 : 12000;
        int maxMsgs  = agent.ShortTermMemoryLimit > 0 ? agent.ShortTermMemoryLimit : 25;
        var history  = GetChatHistory(maxMsgs, maxChars);

        string systemBlock = BuildSystemBlock();
        var snapshot = new List<ChatMessage> { new() { Role = "system", Content = systemBlock } };
        snapshot.AddRange(history.Select(m => new ChatMessage { Role = m.Role, Content = m.Content }));
        return snapshot;
    }

    internal void AddItem(ThreadItem item)
    {
        threadHistory?.Add(item);
        LastMessageAt = DateTime.UtcNow;
        HistoryUpdated?.Invoke();
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
        string  prompt,
        string  username          = "user",
        string? augmentedPrompt   = null,
        string? recallNotes       = null,
        string? contextSummary    = null,
        int     maxTokensOverride = 0)
    {
        LastMessageAt = DateTime.UtcNow;
        bool isDisplayThread = threadHistory is not null;

        List<Attachment> threadAtts;
        List<Attachment> msgAtts;
        lock (attachments)        { threadAtts = attachments.ToList(); }
        lock (pendingMessageAtts) { msgAtts    = pendingMessageAtts.ToList(); }

        // ── Build message list ──────────────────────────────────────────────────
        List<object> messages;

        if (isDisplayThread)
        {
            // Store the clean user message in thread history and notify watchers immediately.
            threadHistory!.Add(new UserMessage
            {
                Username    = username,
                Content     = prompt,
                Timestamp   = DateTime.Now,
                Attachments = msgAtts.Count > 0 ? msgAtts.ToList() : null
            });
            HistoryUpdated?.Invoke();

            int maxChars = agent.MaxContextTokens > 0 ? agent.MaxContextTokens * 2 : 12000;
            int maxMsgs  = agent.ShortTermMemoryLimit > 0 ? agent.ShortTermMemoryLimit : 25;
            var history  = GetChatHistory(maxMsgs, maxChars);

            // Substitute augmented prompt into the last (current) history entry if provided.
            if (augmentedPrompt is not null && history.Count > 0)
            {
                var last = history[^1];
                history[^1] = last with { Content = $"{last.Username}: {augmentedPrompt}" };
            }

            messages = new List<object> { new { role = "system", content = BuildSystemBlock() } };

            // Historical messages — plain string content.
            for (int i = 0; i < history.Count - 1; i++)
            {
                var m = history[i];
                messages.Add(new { role = m.Role, content = m.Content });
            }

            // Current (last) message — prepend thread attachments and inject images as multipart.
            if (history.Count > 0)
            {
                var current = history[^1];
                messages.Add(BuildCurrentUserMessage(current.Content, threadAtts, msgAtts));
            }
        }
        else
        {
            seedMessages!.Add(new ChatMessage { Role = "user", Content = prompt });
            messages = seedMessages.Select(m => (object)new { role = m.Role, content = m.Content }).ToList();
        }

        // ── Call LLM (streaming) ────────────────────────────────────────────────
        int maxTokens = maxTokensOverride != 0 ? maxTokensOverride : agent.MaxTokens;
        object requestBody = new
        {
            model          = agent.ModelString,
            messages,
            stream         = true,
            stream_options = new { include_usage = true },
            max_tokens     = maxTokens,
            temperature    = 0.7,
            top_p          = 0.80,
            top_k          = 20,
            repeat_penalty = 1.0
        };

        string json = JsonSerializer.Serialize(requestBody);
        HttpRequestMessage request = new(HttpMethod.Post, $"{agent.Endpoint}/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            throw new LlmRequestFailedException($"LLM request failed with status: {response.StatusCode}");

        using Stream      stream = await response.Content.ReadAsStreamAsync();
        using StreamReader reader = new(stream);

        StringBuilder contentBuilder  = new();
        StringBuilder thinkingBuilder = new();
        Stopwatch     sw              = Stopwatch.StartNew();
        bool wasThinking      = false;
        int  completionTokens = 0;

        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            string payload = line["data: ".Length..];
            if (payload == "[DONE]") break;

            JsonDocument chunk;
            try { chunk = JsonDocument.Parse(payload); }
            catch { continue; }

            using (chunk)
            {
                if (chunk.RootElement.TryGetProperty("usage", out JsonElement usage))
                    completionTokens = usage.TryGetProperty("completion_tokens", out JsonElement ct) ? ct.GetInt32() : 0;

                if (!chunk.RootElement.TryGetProperty("choices", out JsonElement choices)) continue;
                if (choices.GetArrayLength() == 0) continue;
                JsonElement delta = choices[0].GetProperty("delta");

                if (delta.TryGetProperty("reasoning_content", out JsonElement reasoning))
                {
                    string? thinkDelta = reasoning.GetString();
                    if (!string.IsNullOrEmpty(thinkDelta))
                    {
                        if (!wasThinking)
                        {
                            Common.Logger.LogWarning("[{Agent}] ({Thread}) thinking chain detected — <|think_off|> may not be working.", agent.Name, threadKey);
                            wasThinking = true;
                        }
                        thinkingBuilder.Append(thinkDelta);
                    }
                }

                if (!delta.TryGetProperty("content", out JsonElement contentEl)) continue;
                string? deltaText = contentEl.GetString();
                if (string.IsNullOrEmpty(deltaText)) continue;
                contentBuilder.Append(deltaText);
            }
        }

        sw.Stop();
        string responseText = contentBuilder.ToString();
        if (string.IsNullOrWhiteSpace(responseText))
            throw new LlmRequestFailedException("LLM response was empty.");

        double elapsed   = sw.Elapsed.TotalSeconds;
        double tokPerSec = completionTokens > 0 ? completionTokens / elapsed : 0;

        Common.Logger.LogInformation("[{Agent}] ({Thread}) responded in {Seconds}s ({Tokens} tokens, {TokPerSec} t/s)",
            agent.Name, threadKey, elapsed.ToString("F1"), completionTokens, tokPerSec.ToString("F1"));
        Common.Logger.LogInformation("[{Agent}] ({Thread}) response\n\"{Response}\"",
            agent.Name, threadKey, responseText);

        // ── Store result ────────────────────────────────────────────────────────
        if (isDisplayThread)
        {
            threadHistory!.Add(new AriResponse
            {
                Content         = responseText,
                Timestamp       = DateTime.Now,
                ThinkingSeconds = elapsed,
                RecallNotes     = recallNotes,
                ContextSummary  = contextSummary
            });
            HistoryUpdated?.Invoke();

            conversationTurnCount++;
            ExchangeCompleted?.Invoke(prompt, responseText);

            if (ShouldFireBufferFull())
                BufferFull?.Invoke();
        }
        else
        {
            seedMessages!.Add(new ChatMessage { Role = "assistant", Content = responseText });
        }

        return responseText;
    }

    // ── Private helpers ─────────────────────────────────────────────────────────

    private string BuildSystemBlock()
    {
        string body = ContextPrompt is null ? agent.SystemPrompt : $"{agent.SystemPrompt}\n\n{ContextPrompt}";
        return agent.Think ? body : $"{body}\n<|think_off|>";
    }

    /// <summary>
    /// Builds the current user turn as a multipart content block.
    /// Prepends thread attachments (text as a labelled block, images as image_url parts),
    /// followed by message attachments, then the prompt text.
    /// </summary>
    private static object BuildCurrentUserMessage(
        string promptText,
        List<Attachment> threadAtts,
        List<Attachment> msgAtts)
    {
        var threadImages = threadAtts.Where(a => a.IsImage).ToList();
        var threadTexts  = threadAtts.Where(a => !a.IsImage).ToList();
        var msgImages    = msgAtts.Where(a => a.IsImage).ToList();
        var msgTexts     = msgAtts.Where(a => !a.IsImage).ToList();

        bool hasThreadContent  = threadImages.Count > 0 || threadTexts.Count > 0;
        bool hasMsgContent     = msgImages.Count > 0 || msgTexts.Count > 0;
        bool needsMultipart    = threadImages.Count > 0 || msgImages.Count > 0;

        if (!hasThreadContent && !hasMsgContent)
            return new { role = "user", content = promptText };

        const string divider = "-------------------";
        var contentParts = new List<object>();

        // Thread attachments block
        if (hasThreadContent)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Files attached to this thread]");
            foreach (var a in threadTexts)
            {
                sb.AppendLine($"--- {a.Name} ---");
                sb.AppendLine(a.Content);
                sb.AppendLine("---");
            }
            if (threadTexts.Count > 0) sb.AppendLine(divider);
            contentParts.Add(new { type = "text", text = sb.ToString().TrimEnd() });

            foreach (var a in threadImages)
            {
                string dataUrl = $"data:{a.MimeType ?? "image/jpeg"};base64,{a.Content}";
                contentParts.Add(new { type = "image_url", image_url = new { url = dataUrl } });
            }
        }

        // Message attachments block
        if (hasMsgContent)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Files attached to this message]");
            foreach (var a in msgTexts)
            {
                sb.AppendLine($"--- {a.Name} ---");
                sb.AppendLine(a.Content);
                sb.AppendLine("---");
            }
            if (msgTexts.Count > 0) sb.AppendLine(divider);
            contentParts.Add(new { type = "text", text = sb.ToString().TrimEnd() });

            foreach (var a in msgImages)
            {
                string dataUrl = $"data:{a.MimeType ?? "image/jpeg"};base64,{a.Content}";
                contentParts.Add(new { type = "image_url", image_url = new { url = dataUrl } });
            }
        }

        // Prompt text last
        contentParts.Add(new { type = "text", text = promptText });

        return new { role = "user", content = (object)contentParts };
    }

    private bool ShouldFireBufferFull()
    {
        int limit = agent.ShortTermMemoryLimit > 0 ? agent.ShortTermMemoryLimit : 25;

        if (!bufferEverFilled && conversationTurnCount >= limit)
        {
            bufferEverFilled = true;
            return true;
        }

        if (!bufferEverFilled) return false;
        int interval = Math.Max(1, limit / 2);
        return conversationTurnCount % interval == 0;
    }
}

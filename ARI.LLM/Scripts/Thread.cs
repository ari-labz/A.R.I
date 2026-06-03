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
    private readonly string?           systemContent;
    private int  conversationTurnCount;
    private bool bufferEverFilled;

    // ── AdHoc mode (Engram write threads — LLM-only, no display history) ──────
    private readonly List<ChatMessage>? seedMessages;

    internal DateTime LastMessageAt { get; private set; } = DateTime.MinValue;

    /// <summary>Fires whenever a ThreadItem is added to this thread's history (user message, ARI response, command, engram event).</summary>
    internal event Action? HistoryUpdated;

    /// <summary>Fires when the conversation reaches the agent's memory limit. No payload — Engram re-fetches what it needs.</summary>
    internal event Action? BufferFull;
    internal event Action<string, string>? ExchangeCompleted;

    // ── Constructors ────────────────────────────────────────────────────────────

    /// <summary>Regular display thread — used by Dialogue for user-facing conversations.</summary>
    internal Thread(Agent agent, string threadKey, string? contextNote = null)
    {
        this.agent     = agent;
        this.threadKey = threadKey;
        httpClient     = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        threadHistory  = new List<ThreadItem>();

        string body   = contextNote is null ? agent.SystemPrompt : $"{agent.SystemPrompt}\n\n{contextNote}";
        systemContent = agent.Think ? body : $"{body}\n<|think_off|>";
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

        var snapshot = new List<ChatMessage> { new() { Role = "system", Content = systemContent! } };
        snapshot.AddRange(history.Select(m => new ChatMessage { Role = m.Role, Content = m.Content }));
        return snapshot;
    }

    internal void AddItem(ThreadItem item)
    {
        threadHistory?.Add(item);
        LastMessageAt = DateTime.UtcNow;
        HistoryUpdated?.Invoke();
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

        // ── Build message list ──────────────────────────────────────────────────
        List<object> messages;

        if (isDisplayThread)
        {
            // Store the clean user message in thread history and notify watchers immediately
            // so other connected clients see the incoming message before ARI has responded.
            threadHistory!.Add(new UserMessage { Username = username, Content = prompt, Timestamp = DateTime.Now });
            HistoryUpdated?.Invoke();

            // Derive LLM context window from thread history.
            int maxChars = agent.MaxContextTokens > 0 ? agent.MaxContextTokens * 2 : 12000;
            int maxMsgs  = agent.ShortTermMemoryLimit > 0 ? agent.ShortTermMemoryLimit : 25;
            var history  = GetChatHistory(maxMsgs, maxChars);

            // If recall/context augmentation was applied, substitute it into the last (current) entry.
            if (augmentedPrompt is not null && history.Count > 0)
            {
                var last = history[^1];
                history[^1] = last with { Content = $"{last.Username}: {augmentedPrompt}" };
            }

            messages = new List<object> { new { role = "system", content = systemContent } };
            messages.AddRange(history.Select(m => (object)new { role = m.Role, content = m.Content }));
        }
        else
        {
            // AdHoc thread: append to the pre-seeded message list directly.
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

    // ── Private ─────────────────────────────────────────────────────────────────

    private bool ShouldFireBufferFull()
    {
        int limit = agent.ShortTermMemoryLimit > 0 ? agent.ShortTermMemoryLimit : 25;

        // First time the buffer fills — fire once.
        if (!bufferEverFilled && conversationTurnCount >= limit)
        {
            bufferEverFilled = true;
            return true;
        }

        // Subsequently — fire every limit/2 turns so Engram runs periodically.
        if (!bufferEverFilled) return false;
        int interval = Math.Max(1, limit / 2);
        return conversationTurnCount % interval == 0;
    }
}

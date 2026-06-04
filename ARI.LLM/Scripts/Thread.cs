using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal enum ThreadState { Active, Inactive, Dormant, Deleted }

internal class Thread
{
    private const int MIN_INACTIVITY_TIMER          = 30; // minutes - Default 30
    private const int MIN_DELETION_TIMER            = 15; // minutes - Default 15
    private const int MIN_INACTIVITY_THRESHOLD      = 1;  // minutes — adaptive threshold floor

    private readonly Agent      agent;
    private readonly string     threadKey;
    private readonly HttpClient httpClient;

    internal readonly List<ThreadItem> History;
    private readonly int shortTermMemoryLimit;
    private readonly int maxContextTokens;

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

    // ── Attachments ────────────────────────────────────────────────────────────
    private readonly List<Attachment> attachments        = new();
    private readonly List<Attachment> pendingMessageAtts = new();

    internal string? PlatformContext { get; init; }

    internal DateTime LastMessageAt { get; private set; } = DateTime.MinValue;

    /// <summary>Fires whenever a ThreadItem is added to this thread's history.</summary>
    internal event Action? Updated;

    /// <summary>Fires when the conversation reaches the agent's memory limit.</summary>
    internal event Action? BufferFull;
    internal event Action<string, string>? ExchangeCompleted;
    internal event Action? BecameInactive;
    internal event Action? Deleted;

    // ── Constructors ────────────────────────────────────────────────────────────

    /// <summary>Regular display thread — used by Dialogue for user-facing conversations.</summary>
    internal Thread(Agent agent, string threadKey, string? platformContext = null, int shortTermMemoryLimit = 0, int maxContextTokens = 0)
    {
        this.agent                = agent;
        this.threadKey            = threadKey;
        this.shortTermMemoryLimit = shortTermMemoryLimit;
        this.maxContextTokens     = maxContextTokens;
        httpClient                = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        History             = new List<ThreadItem>();
        PlatformContext           = platformContext;
    }

    // ── Accessors ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Derives the LLM context window from thread history.
    /// Walks backwards, collects items with a non-null Message, stops at maxMessages or maxChars.
    /// Returns the list in chronological order (oldest first).
    /// </summary>

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

    /// <summary>
    /// Returns a snapshot of the current LLM context for seeding Engram write threads.
    /// Contains raw message content — the "Username: " prefix is applied at send time.
    /// </summary>
    internal List<ThreadMessage> SaveContext()
    {
        return GetChatHistory(shortTermMemoryLimit, maxContextTokens > 0 ? maxContextTokens * 2 : 0);
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

    /// <summary>
    /// Pre-loads a saved context snapshot into History.
    /// Used by Engram to fork a fresh write thread from a fixed conversation state —
    /// the snapshot is a real history so subsequent prompts build on it naturally.
    /// </summary>
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
        string  prompt,
        string  username          = "user",
        string? augmentedPrompt   = null,
        string? recallNotes       = null,
        string? contextSummary    = null,
        int     maxTokensOverride = 0)
    {
        LastMessageAt = DateTime.UtcNow;

        // Reactivate from any non-active state when a new message arrives
        if (state != ThreadState.Active)
        {
            inactivityTimer?.Dispose();
            inactivityTimer = null;
            dormantTimer?.Dispose();
            dormantTimer = null;
            state = ThreadState.Active;
        }

        // Record how long the user took to reply since ARI's last response
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
        History.Add(new UserMessage
        {
            Username    = username,
            Content     = prompt,
            Timestamp   = DateTime.Now,
            Attachments = msgAtts.Count > 0 ? msgAtts.ToList() : null
        });
        Updated?.Invoke();

        List<ThreadMessage> history = GetChatHistory(shortTermMemoryLimit, maxContextTokens > 0 ? maxContextTokens * 2 : 0);

        // Substitute augmented prompt into the last (current) history entry if provided.
        if (augmentedPrompt is not null && history.Count > 0)
            history[^1] = history[^1] with { Content = augmentedPrompt };

        List<object> messages = new List<object> { new { role = "system", content = BuildSystemBlock() } };

        for (int i = 0; i < history.Count - 1; i++)
        {
            ThreadMessage m = history[i];
            messages.Add(new { role = m.Role, content = $"{m.Username}: {m.Content}" });
        }

        if (history.Count > 0)
        {
            ThreadMessage current = history[^1];
            messages.Add(BuildCurrentUserMessage($"{current.Username}: {current.Content}", threadAtts, msgAtts));
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
        if (responseText.StartsWith("ARI: ", StringComparison.OrdinalIgnoreCase))
            responseText = responseText["ARI: ".Length..];
        if (string.IsNullOrWhiteSpace(responseText))
            throw new LlmRequestFailedException("LLM response was empty.");

        double elapsed   = sw.Elapsed.TotalSeconds;
        double tokPerSec = completionTokens > 0 ? completionTokens / elapsed : 0;

        Common.Logger.LogInformation("[{Agent}] ({Thread}) responded in {Seconds}s ({Tokens} tokens, {TokPerSec} t/s)",
            agent.Name, threadKey, elapsed.ToString("F1"), completionTokens, tokPerSec.ToString("F1"));
        Common.Logger.LogInformation("[{Agent}] ({Thread}) response\n\"{Response}\"",
            agent.Name, threadKey, responseText);

        // ── Store result ────────────────────────────────────────────────────────
        History.Add(new AriResponse
        {
            Content         = responseText,
            Timestamp       = DateTime.Now,
            ThinkingSeconds = elapsed,
            RecallNotes     = recallNotes,
            ContextSummary  = contextSummary
        });
        Updated?.Invoke();

        // Start the inactivity countdown — ARI has replied, waiting for the user
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
        List<Attachment> threadImages = threadAtts.Where(a => a.IsImage).ToList();
        List<Attachment> threadTexts  = threadAtts.Where(a => !a.IsImage).ToList();
        List<Attachment> msgImages    = msgAtts.Where(a => a.IsImage).ToList();
        List<Attachment> msgTexts     = msgAtts.Where(a => !a.IsImage).ToList();

        bool hasThreadContent  = threadImages.Count > 0 || threadTexts.Count > 0;
        bool hasMsgContent     = msgImages.Count > 0 || msgTexts.Count > 0;
        bool needsMultipart    = threadImages.Count > 0 || msgImages.Count > 0;

        if (!hasThreadContent && !hasMsgContent)
            return new { role = "user", content = promptText };

        const string divider = "-------------------";
        List<object> contentParts = new();

        // Thread attachments block
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

        // Message attachments block
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

        // Prompt text last
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

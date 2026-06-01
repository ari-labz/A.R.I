using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Thread
{
    private const string QwenSystemPrefix = "You are Qwen, created by Alibaba Cloud. You are a helpful assistant.\n\n";

    // Strips Discord-injected prefix, e.g. "[31/05/2026 15:02] [xywren via DM]: "
    private static readonly Regex DiscordPrefixPattern =
        new(@"^\[\d{2}/\d{2}/\d{4} \d{2}:\d{2}\] \[.+?\]: ", RegexOptions.Compiled);

    private readonly Model model;
    private readonly string threadKey;
    private readonly HttpClient httpClient;
    private readonly List<ChatMessage> shortTermMemory;
    private readonly List<ChatMessage> displayHistory = new(); // original prompts, no augmentation

    private int messageCount;
    private bool bufferEverFilled;

    internal DateTime LastMessageAt { get; private set; } = DateTime.MinValue;

    internal event Action<IReadOnlyList<ChatMessage>>? BufferFull;
    internal event Action<string, string>? ExchangeCompleted; // (userMessage, assistantResponse)

    internal Thread(Model model, string threadKey, string? contextNote = null)
    {
        this.model = model;
        this.threadKey = threadKey;
        httpClient = new HttpClient
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        // Qwen3.6 requires this prefix for correct behaviour.
        // <|think_off|> disables chain-of-thought for the whole conversation.
        string systemContent = QwenSystemPrefix +
            (contextNote is null ? model.SystemPrompt : $"{model.SystemPrompt}\n\n{contextNote}") +
            "\n<|think_off|>";

        shortTermMemory = new List<ChatMessage>
        {
            new ChatMessage { Role = "system", Content = systemContent }
        };
    }

    // Seeded constructor — creates a thread pre-loaded with a snapshot of another thread's messages.
    // Used by Engram to fork a ContextCache for per-note write calls.
    internal Thread(Model model, string threadKey, IReadOnlyList<ChatMessage> seedMessages)
    {
        this.model = model;
        this.threadKey = threadKey;
        httpClient = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        shortTermMemory = seedMessages.ToList();
        displayHistory  = new List<ChatMessage>();
    }

    internal IReadOnlyList<ChatMessage> GetHistory() => shortTermMemory.AsReadOnly();
    internal IReadOnlyList<ChatMessage> GetDisplayHistory() => displayHistory.AsReadOnly();
    internal List<ChatMessage> GetShortTermMemoryCopy() => shortTermMemory.ToList();

    internal async Task<string> SendPrompt(string prompt, string? originalUserMessage = null, string? recallNotes = null, string? contextSummary = null, int maxTokensOverride = 0)
    {
        LastMessageAt = DateTime.UtcNow;
        string displayText = originalUserMessage ?? prompt;
        displayText = DiscordPrefixPattern.Replace(displayText, "");
        displayHistory.Add(new ChatMessage { Role = "user", Content = displayText, Timestamp = DateTime.Now });
        shortTermMemory.Add(new ChatMessage { Role = "user", Content = prompt });

        int maxTokens = maxTokensOverride != 0 ? maxTokensOverride : model.MaxTokens;
        object requestBody = new
        {
            model = model.ModelString,
            messages = shortTermMemory,
            stream = true,
            stream_options = new { include_usage = true },
            max_tokens = maxTokens,
            temperature = 0.7,
            top_p = 0.80,
            top_k = 20,
            repeat_penalty = 1.0
        };

        string json = JsonSerializer.Serialize(requestBody);
        HttpRequestMessage request = new(HttpMethod.Post, $"{model.Endpoint}/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            throw new LlmRequestFailedException($"LLM request failed with status: {response.StatusCode}");

        using Stream stream = await response.Content.ReadAsStreamAsync();
        using StreamReader reader = new(stream);

        StringBuilder contentBuilder  = new();
        StringBuilder thinkingBuilder = new();
        Stopwatch sw         = Stopwatch.StartNew();
        bool wasThinking     = false;
        int promptTokens     = 0;
        int completionTokens = 0;

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
                // Usage chunk — sent as the final data frame before [DONE]
                if (chunk.RootElement.TryGetProperty("usage", out JsonElement usage))
                {
                    promptTokens     = usage.TryGetProperty("prompt_tokens",     out JsonElement pt) ? pt.GetInt32() : 0;
                    completionTokens = usage.TryGetProperty("completion_tokens", out JsonElement ct) ? ct.GetInt32() : 0;
                }

                if (!chunk.RootElement.TryGetProperty("choices", out JsonElement choices)) continue;
                if (choices.GetArrayLength() == 0) continue;

                JsonElement delta = choices[0].GetProperty("delta");

                // Thinking content — should not appear with <|think_off|> but handle gracefully
                if (delta.TryGetProperty("reasoning_content", out JsonElement reasoning))
                {
                    string? thinkDelta = reasoning.GetString();
                    if (!string.IsNullOrEmpty(thinkDelta))
                    {
                        if (!wasThinking)
                        {
                            Common.Logger.LogWarning("[{Model}] ({Thread}) thinking chain detected — <|think_off|> may not be working.",
                                model.Name, threadKey);
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

        double elapsed  = sw.Elapsed.TotalSeconds;
        double tokPerSec = completionTokens > 0 ? completionTokens / elapsed : 0;

        Common.Logger.LogInformation("[{Model}] ({Thread}) responded in {Seconds}s ({CompTokens} tokens, {TokPerSec} t/s)",
            model.Name, threadKey,
            elapsed.ToString("F1"),
            completionTokens,
            tokPerSec.ToString("F1"));
        Common.Logger.LogInformation("[{Model}] ({Thread}) response\n\"{Response}\"",
            model.Name, threadKey, responseText);

        shortTermMemory.Add(new ChatMessage { Role = "assistant", Content = responseText });
        displayHistory.Add(new ChatMessage
        {
            Role = "assistant",
            Content = responseText,
            Timestamp = DateTime.Now,
            ThinkingSeconds = elapsed,
            RecallNotes = recallNotes,
            ContextSummary = contextSummary
        });
        messageCount++;

        ExchangeCompleted?.Invoke(prompt, responseText);

        // Fire BEFORE trimming so Engram sees the full history including messages about to fall off.
        if (ShouldFireBufferFull())
            BufferFull?.Invoke(shortTermMemory.AsReadOnly());

        TrimShortTermMemory();

        return responseText;
    }

    private void TrimShortTermMemory()
    {
        // Pass 1 — message-count cap.
        if (model.ShortTermMemoryLimit > 0 && shortTermMemory.Count > model.ShortTermMemoryLimit + 1)
        {
            shortTermMemory.RemoveRange(1, shortTermMemory.Count - model.ShortTermMemoryLimit - 1);
            bufferEverFilled = true;
        }

        // Pass 2 — token-budget cap (1 token ≈ 4 chars).
        // Drops the oldest non-system messages until the estimated token count is within budget.
        // Preserves at least the system message + the most recent exchange.
        if (model.MaxContextTokens > 0)
        {
            int budgetChars = model.MaxContextTokens * 4;
            while (shortTermMemory.Count > 3) // system + at least one user/assistant pair
            {
                int totalChars = shortTermMemory.Sum(m => m.Content?.Length ?? 0);
                if (totalChars <= budgetChars) break;
                shortTermMemory.RemoveAt(1); // drop oldest non-system message
                bufferEverFilled = true;
            }
        }
    }

    private bool ShouldFireBufferFull()
    {
        if (model.ShortTermMemoryLimit == 0) return false;

        // First overflow — about to trim for the first time. Always fire.
        bool isFirstOverflow = !bufferEverFilled && shortTermMemory.Count > model.ShortTermMemoryLimit + 1;
        if (isFirstOverflow) return true;

        // Subsequent sweeps — fire every limit/2 messages after the buffer first filled.
        if (!bufferEverFilled) return false;
        int interval = Math.Max(1, model.ShortTermMemoryLimit / 2);
        return messageCount % interval == 0;
    }

}

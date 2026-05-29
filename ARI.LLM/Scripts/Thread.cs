using System.Text;
using System.Text.Json;

namespace ARI.LLM;

internal class Thread
{
    private readonly Model model;
    private readonly HttpClient httpClient;
    private readonly List<ChatMessage> shortTermMemory;

    private int messageCount;
    private bool bufferEverFilled;

    internal DateTime LastMessageAt { get; private set; } = DateTime.MinValue;

    internal event Action<IReadOnlyList<ChatMessage>>? BufferFull;

    internal Thread(Model model, string? contextNote = null)
    {
        this.model = model;
        httpClient = new HttpClient
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        string systemContent = contextNote is null
            ? model.SystemPrompt
            : $"{model.SystemPrompt}\n\n{contextNote}";

        shortTermMemory = new List<ChatMessage>
        {
            new ChatMessage { Role = "system", Content = systemContent }
        };
    }

    internal IReadOnlyList<ChatMessage> GetHistory() => shortTermMemory.AsReadOnly();

    internal async Task<string> SendPrompt(string prompt)
    {
        LastMessageAt = DateTime.UtcNow;
        shortTermMemory.Add(new ChatMessage { Role = "user", Content = prompt });

        object requestBody = new
        {
            model = model.ModelString,
            messages = shortTermMemory,
            stream = false,
            think = false
        };

        string json = JsonSerializer.Serialize(requestBody);
        StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await httpClient.PostAsync($"{model.Endpoint}/api/chat", content);

        if (!response.IsSuccessStatusCode)
            throw new LlmRequestFailedException($"LLM request failed with status: {response.StatusCode}");

        string responseJson = await response.Content.ReadAsStringAsync();

        JsonDocument document = JsonDocument.Parse(responseJson);
        string responseText = document.RootElement
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?? throw new LlmRequestFailedException("LLM response was empty.");

        shortTermMemory.Add(new ChatMessage { Role = "assistant", Content = responseText });
        TrimShortTermMemory();

        messageCount++;
        if (ShouldFireBufferFull())
            BufferFull?.Invoke(shortTermMemory.AsReadOnly());

        return responseText;
    }

    private void TrimShortTermMemory()
    {
        if (model.ShortTermMemoryLimit == 0) return; // 0 = unlimited
        if (shortTermMemory.Count > model.ShortTermMemoryLimit + 1)
        {
            shortTermMemory.RemoveRange(1, shortTermMemory.Count - model.ShortTermMemoryLimit - 1);
            bufferEverFilled = true;
        }
    }

    private bool ShouldFireBufferFull()
    {
        if (!bufferEverFilled) return false;

        // Fire once when the buffer first fills, then every limit/2 messages
        int interval = Math.Max(1, model.ShortTermMemoryLimit / 2);
        return messageCount % interval == 0;
    }
}

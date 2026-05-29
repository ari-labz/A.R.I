using System.Text;
using System.Text.Json;

namespace ARI.LLM;

internal class Thread
{
    private readonly Model model;
    private readonly HttpClient httpClient;
    private readonly List<ChatMessage> shortTermMemory;

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

    internal async Task<string> SendPrompt(string prompt)
    {
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

        return responseText;
    }

    private void TrimShortTermMemory()
    {
        // System message at index 0 is never trimmed
        if (shortTermMemory.Count > model.ShortTermMemoryLimit + 1)
            shortTermMemory.RemoveRange(1, shortTermMemory.Count - model.ShortTermMemoryLimit - 1);
    }
}

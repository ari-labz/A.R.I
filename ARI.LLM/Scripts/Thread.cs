using System.Text;
using System.Text.Json;

namespace ARI.LLM;

internal class Thread
{
    private readonly Model model;
    private readonly HttpClient httpClient;
    private readonly List<ChatMessage> history;

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

        history = new List<ChatMessage>
        {
            new ChatMessage { Role = "system", Content = systemContent }
        };
    }

    internal async Task<string> SendPrompt(string prompt)
    {
        history.Add(new ChatMessage { Role = "user", Content = prompt });

        object requestBody = new
        {
            model = model.ModelString,
            messages = history,
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

        history.Add(new ChatMessage { Role = "assistant", Content = responseText });
        TrimHistory();

        return responseText;
    }

    private void TrimHistory()
    {
        // System message at index 0 is never trimmed
        if (history.Count > model.HistoryLimit + 1)
            history.RemoveRange(1, history.Count - model.HistoryLimit - 1);
    }
}

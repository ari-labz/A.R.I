using System.Text;
using System.Text.Json;

namespace ARI.LLM;

internal class ModelClient
{
    private readonly string endpoint;
    private readonly string model;
    private readonly int historyLimit;
    private readonly HttpClient httpClient;
    private readonly List<ChatMessage> history;

    internal ModelClient(string endpoint, string model, string systemPrompt, int historyLimit)
    {
        this.endpoint = endpoint;
        this.model = model;
        this.historyLimit = historyLimit;
        httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        history = new List<ChatMessage>
        {
            new ChatMessage { Role = "system", Content = systemPrompt }
        };
    }

    internal async Task<string> SendPrompt(string prompt)
    {
        history.Add(new ChatMessage { Role = "user", Content = prompt });

        object requestBody = new
        {
            model,
            messages = history,
            stream = false
        };

        string json = JsonSerializer.Serialize(requestBody);
        StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await httpClient.PostAsync($"{endpoint}/api/chat", content);

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
        if (history.Count > historyLimit + 1)
            history.RemoveRange(1, history.Count - historyLimit - 1);
    }
}

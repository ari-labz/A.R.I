using System.Text;
using System.Text.Json;

namespace ARI.LLM;

internal class ModelClient
{
    private readonly string endpoint;
    private readonly string model;
    private readonly string systemPrompt;
    private readonly HttpClient httpClient;

    internal ModelClient(string endpoint, string model, string systemPrompt)
    {
        this.endpoint = endpoint;
        this.model = model;
        this.systemPrompt = systemPrompt;
        httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal async Task<string> SendPrompt(string prompt, string? context = null)
    {
        object requestBody = new
        {
            model,
            prompt,
            system = BuildSystemPrompt(context),
            stream = false
        };

        string json = JsonSerializer.Serialize(requestBody);
        StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await httpClient.PostAsync($"{endpoint}/api/generate", content);

        if (!response.IsSuccessStatusCode)
            throw new LlmRequestFailedException($"LLM request failed with status: {response.StatusCode}");

        string responseJson = await response.Content.ReadAsStringAsync();

        JsonDocument document = JsonDocument.Parse(responseJson);
        string responseText = document.RootElement.GetProperty("response").GetString()
                              ?? throw new LlmRequestFailedException("LLM response was empty.");

        return responseText;
    }

    private string BuildSystemPrompt(string? context)
    {
        if (string.IsNullOrEmpty(systemPrompt) && string.IsNullOrEmpty(context))
            return string.Empty;

        if (string.IsNullOrEmpty(context))
            return systemPrompt;

        if (string.IsNullOrEmpty(systemPrompt))
            return context;

        return $"{systemPrompt}\n{context}";
    }
}

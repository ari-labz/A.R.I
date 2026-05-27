using System.Text;
using System.Text.Json;

namespace ARI.Core.LLM;

public class LlmService
{
    private readonly string endpoint;
    private readonly string model;
    private readonly HttpClient httpClient;

    public LlmService(string endpoint, string model)
    {
        this.endpoint = endpoint;
        this.model = model;
        httpClient = new HttpClient();
    }

    public async Task<string> SendMessage(string prompt)
    {
        object requestBody = new
        {
            model = model,
            prompt = prompt,
            stream = false
        };

        string json = JsonSerializer.Serialize(requestBody);
        StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

        Console.WriteLine($"Sending prompt to LLM: {prompt}");

        HttpResponseMessage response = await httpClient.PostAsync($"{endpoint}/api/generate", content);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"LLM request failed with status: {response.StatusCode}");
            throw new Exception($"LLM request failed with status: {response.StatusCode}");
        }

        string responseJson = await response.Content.ReadAsStringAsync();

        JsonDocument document = JsonDocument.Parse(responseJson);
        string responseText = document.RootElement.GetProperty("response").GetString()
                              ?? throw new Exception("LLM response was empty.");

        Console.WriteLine($"LLM response received.");
        return responseText;
    }
}
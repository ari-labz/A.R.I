using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Context : Model
{
    private readonly HttpClient httpClient;
    private readonly Dictionary<string, string> contexts = new();
    private readonly SemaphoreSlim updateLock = new(1, 1);
    private readonly string resolvedPrompt;

    internal Context(ModelConfig config, int shortTermMemoryLimit) : base(config)
    {
        httpClient     = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        resolvedPrompt = config.SystemPrompt.Replace("{memoryLimit}", shortTermMemoryLimit.ToString());
    }

    internal string GetContext(string threadKey)
        => contexts.TryGetValue(threadKey, out string? ctx) ? ctx : string.Empty;

    /// <summary>
    /// Updates the context summary after each Dialogue exchange.
    /// Fire-and-forget safe — errors are logged, never thrown.
    /// </summary>
    internal async Task UpdateAsync(string threadKey, string userMessage, string assistantResponse)
    {
        await updateLock.WaitAsync();
        try
        {
            string current = GetContext(threadKey);
            string updated = await CallContextLlmAsync(current, userMessage, assistantResponse);
            if (!string.IsNullOrWhiteSpace(updated))
            {
                contexts[threadKey] = updated;
                Common.Logger.LogInformation("[Context] ({Thread}) context updated:\n{Context}", threadKey, updated);
            }
        }
        catch (Exception ex)
        {
            Common.Logger.LogWarning("[Context] Failed to update context for [{ThreadKey}]: {Error}", threadKey, ex.Message);
        }
        finally
        {
            updateLock.Release();
        }
    }

    /// <summary>
    /// Rebuilds the context summary from the full conversation transcript.
    /// Called by Engram before extraction so Context has the complete untrimmed history.
    /// </summary>
    internal async Task RebuildFromTranscriptAsync(string threadKey, string transcript)
    {
        await updateLock.WaitAsync();
        try
        {
            string current = GetContext(threadKey);
            Common.Logger.LogInformation("[Engram] [Context] analysing...");
            (string updated, int tokens, double elapsed) = await CallContextFromTranscriptAsync(current, transcript);

            double tokPerSec = elapsed > 0 && tokens > 0 ? tokens / elapsed : 0;

            if (!string.IsNullOrWhiteSpace(updated))
            {
                contexts[threadKey] = updated;
                Common.Logger.LogInformation("[Engram] [Context] analysed context ({Tokens} tokens, {TokPerSec} t/s)\n{Context}",
                    tokens, tokPerSec.ToString("F1"), updated);
            }
            else
            {
                Common.Logger.LogInformation("[Engram] [Context] no context generated ({Tokens} tokens, {TokPerSec} t/s)",
                    tokens, tokPerSec.ToString("F1"));
            }
        }
        catch (Exception ex)
        {
            Common.Logger.LogWarning("[Engram] [Context] failed to rebuild context for [{ThreadKey}]: {Error}", threadKey, ex.Message);
        }
        finally
        {
            updateLock.Release();
        }
    }

    // ── Private ──────────────────────────────────────────────────────────────────

    private async Task<(string content, int tokens, double elapsed)> CallContextFromTranscriptAsync(string currentContext, string transcript)
    {
        string contextBlock = string.IsNullOrWhiteSpace(currentContext) ? "No prior context." : currentContext;

        object requestBody = new
        {
            model    = ModelString,
            messages = new[]
            {
                new { role = "system", content = resolvedPrompt + "\n<|think_off|>" },
                new { role = "user",   content =
                    $"CURRENT CONTEXT:\n{contextBlock}\n\n" +
                    $"FULL CONVERSATION:\n{transcript}\n\n" +
                    "Produce an updated context summary covering this full conversation." }
            },
            stream         = false,
            max_tokens     = 400,
            temperature    = 0.3,
            top_p          = 0.95,
            top_k          = 20,
            repeat_penalty = 1.0
        };

        string json = JsonSerializer.Serialize(requestBody);
        HttpRequestMessage request = new(HttpMethod.Post, $"{Endpoint}/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        Stopwatch sw = Stopwatch.StartNew();
        HttpResponseMessage response = await httpClient.SendAsync(request);
        sw.Stop();
        response.EnsureSuccessStatusCode();

        string responseJson = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(responseJson);
        JsonElement root = doc.RootElement;

        string content = root.GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

        int tokens = root.TryGetProperty("usage", out JsonElement usage) &&
                     usage.TryGetProperty("completion_tokens", out JsonElement ct)
            ? ct.GetInt32() : 0;

        return (content, tokens, sw.Elapsed.TotalSeconds);
    }

    private async Task<string> CallContextLlmAsync(string currentContext, string userMessage, string assistantResponse)
    {
        string contextBlock = string.IsNullOrWhiteSpace(currentContext)
            ? "No context yet — this is the first exchange."
            : currentContext;

        object requestBody = new
        {
            model    = ModelString,
            messages = new[]
            {
                new { role = "system", content = resolvedPrompt + "\n<|think_off|>" },
                new { role = "user",   content =
                    $"CURRENT CONTEXT:\n{contextBlock}\n\n" +
                    $"NEW EXCHANGE:\nWren: {userMessage}\nARI: {assistantResponse}\n\n" +
                    "Update the context summary." }
            },
            stream         = false,
            max_tokens     = 400,
            temperature    = 0.3,
            top_p          = 0.95,
            top_k          = 20,
            repeat_penalty = 1.0
        };

        string json = JsonSerializer.Serialize(requestBody);
        HttpRequestMessage request = new(HttpMethod.Post, $"{Endpoint}/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        string responseJson = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(responseJson);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }
}

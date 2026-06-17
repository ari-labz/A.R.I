using ARI.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Context : Agent
{
    private const int    CONTEXT_MAX_TOKENS = 400;
    private const double CONTEXT_TEMPERATURE = 0.3;
    private const double CONTEXT_TOP_P       = 0.95;
    private const int    CONTEXT_TOP_K       = 20;
    private const double CONTEXT_REPEAT      = 1.0;

    private readonly HttpClient httpClient;
    private readonly Dictionary<string, string> contexts = new();
    private readonly SemaphoreSlim updateLock = new(1, 1);
    private string resolvedPrompt;

    internal override ThreadType Type => ThreadType.Context;

    internal Context()
    {
        httpClient = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        resolvedPrompt = "";
    }

    internal void Init(int shortTermMemoryLimit)
    {
        resolvedPrompt = SystemPrompt.Replace("{memoryLimit}", shortTermMemoryLimit.ToString());
    }

    internal string GetContext(string threadKey)
        => contexts.TryGetValue(threadKey, out string? ctx) ? ctx : string.Empty;

    internal async Task Update(string threadKey, string userMessage, string assistantResponse)
    {
        await updateLock.WaitAsync();
        try
        {
            string contextBlock = string.IsNullOrWhiteSpace(contexts.GetValueOrDefault(threadKey))
                ? "No context yet — this is the first exchange."
                : contexts[threadKey];

            object body = new
            {
                model    = "local",
                messages = new[]
                {
                    new { role = "system", content = $"{resolvedPrompt}\n<|think_off|>" },
                    new { role = "user",   content =
                        $"TODAY: {DateTime.Now:dddd, d MMMM yyyy}\n\n" +
                        $"CURRENT CONTEXT:\n{contextBlock}\n\n" +
                        $"NEW EXCHANGE:\nWren: {userMessage}\nARI: {assistantResponse}\n\n" +
                        "Update the context summary." }
                },
                stream         = false,
                max_tokens     = CONTEXT_MAX_TOKENS,
                temperature    = CONTEXT_TEMPERATURE,
                top_p          = CONTEXT_TOP_P,
                top_k          = CONTEXT_TOP_K,
                repeat_penalty = CONTEXT_REPEAT
            };

            HttpRequestMessage request = new(HttpMethod.Post, $"{Endpoint}/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string responseJson = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(responseJson);
            string updated = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(updated))
            {
                contexts[threadKey] = updated;
                Shared.Logger.LogInformation("[Context] ({Thread}) context updated:\n{Context}", threadKey, updated);
            }
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[Context] Failed to update context for [{ThreadKey}]: {Error}", threadKey, ex.Message);
        }
        finally
        {
            updateLock.Release();
        }
    }

    internal async Task RebuildFromTranscript(string threadKey, string transcript)
    {
        await updateLock.WaitAsync();
        try
        {
            string contextBlock = string.IsNullOrWhiteSpace(contexts.GetValueOrDefault(threadKey))
                ? "No prior context."
                : contexts[threadKey];

            object body = new
            {
                model    = "local",
                messages = new[]
                {
                    new { role = "system", content = $"{resolvedPrompt}\n<|think_off|>" },
                    new { role = "user",   content =
                        $"TODAY: {DateTime.Now:dddd, d MMMM yyyy}\n\n" +
                        $"CURRENT CONTEXT:\n{contextBlock}\n\n" +
                        $"FULL CONVERSATION:\n{transcript}\n\n" +
                        "Produce an updated context summary covering this full conversation." }
                },
                stream         = false,
                max_tokens     = CONTEXT_MAX_TOKENS,
                temperature    = CONTEXT_TEMPERATURE,
                top_p          = CONTEXT_TOP_P,
                top_k          = CONTEXT_TOP_K,
                repeat_penalty = CONTEXT_REPEAT
            };

            HttpRequestMessage request = new(HttpMethod.Post, $"{Endpoint}/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };

            Shared.Logger.LogInformation("[Engram] [Context] analysing...");
            Stopwatch sw = Stopwatch.StartNew();
            HttpResponseMessage response = await httpClient.SendAsync(request);
            sw.Stop();
            response.EnsureSuccessStatusCode();

            string responseJson = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(responseJson);
            JsonElement root = doc.RootElement;

            string updated = root.GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            int tokens = root.TryGetProperty("usage", out JsonElement usage) &&
                         usage.TryGetProperty("completion_tokens", out JsonElement ct)
                ? ct.GetInt32() : 0;

            double tokPerSec = sw.Elapsed.TotalSeconds > 0 && tokens > 0 ? tokens / sw.Elapsed.TotalSeconds : 0;

            if (!string.IsNullOrWhiteSpace(updated))
            {
                contexts[threadKey] = updated;
                Shared.Logger.LogInformation("[Engram] [Context] analysed context ({Tokens} tokens, {TokPerSec} t/s)\n{Context}",
                    tokens, tokPerSec.ToString("F1"), updated);
            }
            else
            {
                Shared.Logger.LogInformation("[Engram] [Context] no context generated ({Tokens} tokens, {TokPerSec} t/s)",
                    tokens, tokPerSec.ToString("F1"));
            }
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[Engram] [Context] failed to rebuild context for [{ThreadKey}]: {Error}", threadKey, ex.Message);
        }
        finally
        {
            updateLock.Release();
        }
    }
}

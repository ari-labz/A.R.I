using ARI.Brain;
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

    /// <summary>Raised (threadKey, title) when an update yields a fresh 3-4 word thread title. Wired by LLMModule to rename the thread.</summary>
    internal Action<string, string>? TitleUpdated;

    public Context()
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

    /// <summary>Pulls a trailing "TITLE: ..." line out of the model's summary output, returning a cleaned
    /// 3-4 word title (or null if absent) and rewriting <paramref name="summary"/> without that line.</summary>
    private static string? ExtractTitle(ref string summary)
    {
        string[] lines = summary.Replace("\r\n", "\n").Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            int idx = line.IndexOf("TITLE:", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;   // last non-empty line isn't a title line — give up

            string title = line[(idx + "TITLE:".Length)..].Trim().Trim('"', '\'', '.', '*', '#').Trim();
            summary = string.Join("\n", lines.Take(i)).TrimEnd();
            // Guard against a runaway/empty title; cap to a sane length.
            if (title.Length == 0 || title.Length > 60) return null;
            return title;
        }
        return null;
    }

    internal async Task Update(string threadKey, string userMessage, string assistantResponse, string username = "User")
    {
        await updateLock.WaitAsync();
        try
        {
            string contextBlock = string.IsNullOrWhiteSpace(contexts.GetValueOrDefault(threadKey))
                ? "No context yet — this is the first exchange."
                : contexts[threadKey];

            // If the brain has a note for this user (matched by title or alias), inject it so the
            // context model has explicit identity/pronoun facts rather than inferring them.
            Note? userNote = BrainModule.GetNote(username);
            string userProfile = userNote is not null
                ? $"USER PROFILE ({username}):\n{userNote.ToPrompt()}\n\n"
                : string.Empty;

            object body = new
            {
                model    = "local",
                messages = new[]
                {
                    new { role = "system", content = $"{resolvedPrompt}\n<|think_off|>" },
                    new { role = "user",   content =
                        $"TODAY: {DateTime.Now:dddd, d MMMM yyyy}\n\n" +
                        userProfile +
                        $"CURRENT CONTEXT:\n{contextBlock}\n\n" +
                        $"NEW EXCHANGE:\n{username}: {userMessage}\nARI: {assistantResponse}\n\n" +
                        "Update the context summary. Then, on a final separate line, write exactly " +
                        "\"TITLE: \" followed by a 3-4 word title naming this conversation's topic." }
                },
                stream         = false,
                max_tokens     = CONTEXT_MAX_TOKENS,
                temperature    = CONTEXT_TEMPERATURE,
                top_p          = CONTEXT_TOP_P,
                top_k          = CONTEXT_TOP_K,
                repeat_penalty = CONTEXT_REPEAT,
                // The template force-opens a <think> block unless disabled via kwargs — the in-band
                // <|think_off|> text alone doesn't stop it, and thinking eats the token budget.
                thinking             = false,
                enable_thinking      = false,
                chat_template_kwargs = new { enable_thinking = false }
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
                // Split off the trailing "TITLE: ..." line so the stored summary stays clean; use the
                // title to rename the thread. If the model omits it, we simply keep the previous title.
                string? title = ExtractTitle(ref updated);
                contexts[threadKey] = updated;
                Shared.Logger.LogInformation("[Context] ({Thread}) context updated:\n{Context}", threadKey, updated);
                if (title is not null) TitleUpdated?.Invoke(threadKey, title);
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
                repeat_penalty = CONTEXT_REPEAT,
                thinking             = false,
                enable_thinking      = false,
                chat_template_kwargs = new { enable_thinking = false }
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

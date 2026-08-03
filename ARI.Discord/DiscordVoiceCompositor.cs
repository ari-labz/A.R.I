using ARI.LLM;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace ARI.Discord;

/// <summary>
/// Collects per-user Whisper transcript lines, assembles them into a labelled script, and fires
/// speculative LLM calls as the conversation unfolds. One compositor per voice channel session.
///
/// Flow:
///   Each user's final transcript → AddLine() → script grows
///   On each new line → cancel previous speculative call, start a new one with the full script
///   After <see cref="SilenceMs"/> ms with no new line from any user → fire the committed response
/// </summary>
internal sealed class DiscordVoiceCompositor : IDisposable
{
    private const int SilenceMs = 2000;

    private readonly LLMModule llm;
    private readonly string threadKey;
    private readonly string platformContext;
    private readonly Func<string, Task> sendReply;
    private readonly ILogger? logger;

    private readonly List<(string UserId, string Text, DateTime At)> lines = new();
    private readonly object _lock = new();

    private CancellationTokenSource? speculativeCts;
    private System.Threading.Timer? silenceTimer;

    internal DiscordVoiceCompositor(
        LLMModule llm,
        string threadKey,
        string platformContext,
        Func<string, Task> sendReply,
        ILogger? logger)
    {
        this.llm             = llm;
        this.threadKey       = threadKey;
        this.platformContext = platformContext;
        this.sendReply       = sendReply;
        this.logger          = logger;
    }

    /// <summary>Called by each user's audio session when Whisper emits a final transcript line.</summary>
    internal void AddLine(string userId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        string script;
        lock (_lock)
        {
            lines.Add((userId, text.Trim(), DateTime.UtcNow));
            script = BuildScript();
            // Reset the 2-second committed-response timer.
            silenceTimer?.Dispose();
            silenceTimer = new System.Threading.Timer(_ => OnSilence(), null, SilenceMs, Timeout.Infinite);
        }

        // Cancel the previous speculative call and start a fresh one with the updated script.
        StartSpeculative(script);
    }

    private void StartSpeculative(string script)
    {
        CancellationTokenSource newCts = new();
        CancellationTokenSource? old;
        lock (_lock) { old = speculativeCts; speculativeCts = newCts; }
        old?.Cancel();
        old?.Dispose();

        string prompt = BuildPrompt(script, stillTalking: true);
        _ = Task.Run(async () =>
        {
            try
            {
                // Fire with voice priority. The result is discarded if a newer speculative call
                // supersedes this one (the CTS is cancelled). If the user goes silent before a
                // newer call arrives, OnSilence() lets this one complete naturally.
                await llm.PromptStreaming(
                    threadKey, prompt, username: "voice",
                    platformContext: platformContext,
                    onDelta: _ => Task.CompletedTask,   // partials go to speech pipeline later; for now we just want the full response ready
                    ct: newCts.Token,
                    priority: InferencePriority.Voice);
            }
            catch (OperationCanceledException) { /* superseded — expected */ }
            catch (Exception ex) { logger?.LogWarning(ex, "[VoiceCompositor] speculative call failed."); }
        }, newCts.Token);
    }

    private void OnSilence()
    {
        string script;
        CancellationTokenSource? currentCts;
        lock (_lock)
        {
            script     = BuildScript();
            currentCts = speculativeCts;
            speculativeCts = null; // claim it — we're committing this response
        }

        // If a speculative call is still running it likely has most of the response already —
        // cancel it and do one clean committed call so we get the full response text to send.
        currentCts?.Cancel();
        currentCts?.Dispose();

        if (string.IsNullOrWhiteSpace(script)) return;

        string prompt = BuildPrompt(script, stillTalking: false);
        logger?.LogInformation("[VoiceCompositor] committing response for script:\n{Script}", script);

        _ = Task.Run(async () =>
        {
            try
            {
                string response = await llm.Prompt(
                    threadKey, prompt,
                    username:        "voice",
                    platformContext: platformContext,
                    priority:        InferencePriority.Voice);

                response = response.Replace("<!--ari-batch-end-->", "").Trim();
                if (string.IsNullOrEmpty(response) || response.Equals("[PASS]", StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogInformation("[VoiceCompositor] no response (pass).");
                    return;
                }

                await sendReply(response);

                // Clear the buffer after a committed response.
                lock (_lock) { lines.Clear(); }
            }
            catch (OperationCanceledException) { /* session ended */ }
            catch (Exception ex) { logger?.LogWarning(ex, "[VoiceCompositor] committed call failed."); }
        });
    }

    private string BuildScript()
    {
        if (lines.Count == 0) return "";
        return string.Join("\n", lines.Select(l => $"{l.UserId}: {l.Text}"));
    }

    private static string BuildPrompt(string script, bool stillTalking)
    {
        string suffix = stillTalking
            ? "\n[Note: the speaker(s) may still be talking — this is a speculative pre-think. Prepare a response but do not commit yet.]"
            : "";
        return $"[Voice conversation transcript]\n{script}{suffix}";
    }

    public void Dispose()
    {
        lock (_lock)
        {
            silenceTimer?.Dispose();
            speculativeCts?.Cancel();
            speculativeCts?.Dispose();
        }
    }
}

using ARI.LLM;
using Discord.Audio;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace ARI.Discord;

/// <summary>
/// Owns the inbound audio pipeline for one voice channel session. Subscribes to
/// <see cref="IAudioClient.StreamCreated"/> and <see cref="IAudioClient.StreamDestroyed"/>;
/// spins up a <see cref="DiscordUserAudioSession"/> per user and tears it down when they leave.
/// </summary>
internal sealed class DiscordVoiceReceiver : IAsyncDisposable
{
    private readonly IAudioClient audioClient;
    private readonly DiscordVoiceCompositor compositor;
    private readonly string whisperUrl;
    private readonly ILogger? logger;

    private readonly Dictionary<ulong, DiscordUserAudioSession> sessions = new();
    private readonly SemaphoreSlim sessionsLock = new(1, 1);

    internal DiscordVoiceReceiver(
        IAudioClient audioClient,
        LLMModule llm,
        string threadKey,
        string platformContext,
        Func<string, Task> sendReply,
        string whisperUrl,
        ILogger? logger)
    {
        this.audioClient = audioClient;
        this.whisperUrl  = whisperUrl;
        this.logger      = logger;

        compositor = new DiscordVoiceCompositor(llm, threadKey, platformContext, sendReply, logger);

        audioClient.StreamCreated   += OnStreamCreated;
        audioClient.StreamDestroyed += OnStreamDestroyed;
    }

    private Task OnStreamDestroyed(ulong userId)
    {
        _ = Task.Run(async () =>
        {
            await sessionsLock.WaitAsync();
            try
            {
                if (sessions.TryGetValue(userId, out DiscordUserAudioSession? old))
                {
                    sessions.Remove(userId);
                    _ = old.DisposeAsync().AsTask();
                    logger?.LogInformation("[VoiceReceiver] session torn down for user {UserId} (StreamDestroyed).", userId);
                }
            }
            finally { sessionsLock.Release(); }
        });
        return Task.CompletedTask;
    }

    private Task OnStreamCreated(ulong userId, AudioInStream stream)
    {
        _ = Task.Run(async () =>
        {
            // Wait for DAVE E2EE handshake to settle before starting the decoder — frames arriving
            // before the encryption layer is ready come through malformed and crash OpusDecodeStream.
            await Task.Delay(1500);

            await sessionsLock.WaitAsync();
            try
            {
                if (sessions.TryGetValue(userId, out DiscordUserAudioSession? old))
                {
                    sessions.Remove(userId);
                    _ = old.DisposeAsync().AsTask();
                }
                DiscordUserAudioSession session = new(userId.ToString(), stream, whisperUrl, compositor, logger);
                sessions[userId] = session;
                session.Start();
                logger?.LogInformation("[VoiceReceiver] started audio session for user {UserId}.", userId);
            }
            finally { sessionsLock.Release(); }
        });
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        audioClient.StreamCreated   -= OnStreamCreated;
        audioClient.StreamDestroyed -= OnStreamDestroyed;

        await sessionsLock.WaitAsync();
        try
        {
            foreach (DiscordUserAudioSession s in sessions.Values)
                await s.DisposeAsync();
            sessions.Clear();
        }
        finally { sessionsLock.Release(); }

        compositor.Dispose();
        sessionsLock.Dispose();
    }
}

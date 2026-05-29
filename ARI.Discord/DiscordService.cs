using ARI.LLM;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ARI.Discord;

public class DiscordService : BackgroundService
{
    private const int MAX_MESSAGE_LENGTH = 2000;
    private const int MESSAGE_SEND_DELAY_MS = 500;

    private readonly DiscordSocketClient client;
    private readonly DiscordConfig config;
    private readonly LlmService llmService;

    public DiscordService(ILoggerFactory loggerFactory, LlmService llmService)
    {
        Common.InitialiseLogger(loggerFactory);
        Common.Logger.LogInformation("Initialising Discord...");

        this.llmService = llmService;

        string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        config = DiscordConfig.LoadFrom(Path.Combine(executableDirectory, "DiscordConfig.json"));

        client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
        });

        client.Log += LogAsync;
        client.Ready += OnReadyAsync;
        client.MessageReceived += message => { _ = Task.Run(() => OnMessageReceivedAsync(message)); return Task.CompletedTask; };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Common.Logger.LogInformation("Connecting to Discord...");
        await client.LoginAsync(TokenType.Bot, config.Token);
        await client.StartAsync();

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private const string PassToken = "[PASS]";

    private const string ServerContextPrompt =
        "You are present in a Discord server. Each message shows who is speaking and in which channel. " +
        "If the conversation was clearly not directed at you and you don't need to be involved, reply with only: [PASS] — nothing else. " +
        "Otherwise, reply normally.";

    public async Task NotifyOfflineAsync()
    {
        Common.Logger.LogInformation("Notifying owner that ARI is going offline...");

        IUser owner = await client.GetUserAsync(config.OwnerId);
        IDMChannel dm = await owner.CreateDMChannelAsync();
        await dm.SendMessageAsync("A.R.I is offline.");
    }

    private async Task OnReadyAsync()
    {
        Common.Logger.LogInformation("Discord bot is ready. Notifying owner...");

        IUser owner = await client.GetUserAsync(config.OwnerId);
        IDMChannel dm = await owner.CreateDMChannelAsync();
        await dm.SendMessageAsync("A.R.I is online.");
        Common.Logger.LogInformation("Sent online notification to owner {OwnerId}", config.OwnerId);
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;

        string conversationKey;
        string contextualPrompt;

        if (message.Channel is IDMChannel)
        {
            // Only the owner can DM Ari directly
            if (message.Author.Id != config.OwnerId)
            {
                Common.Logger.LogDebug("Ignored DM from non-owner user {UserId}", message.Author.Id);
                return;
            }

            conversationKey = $"dm:{message.Author.Id}";
            contextualPrompt = $"[{message.Author.Username} via DM]: {message.Content}";
        }
        else if (message.Channel is SocketGuildChannel guildChannel)
        {
            // Only whitelisted users get responses in servers
            if (!config.WhitelistedUserIds.Contains(message.Author.Id))
            {
                Common.Logger.LogDebug("Ignored server message from non-whitelisted user {UserId}", message.Author.Id);
                return;
            }

            // Respect the allowed guild list if configured
            if (config.AllowedGuildIds.Count > 0 && !config.AllowedGuildIds.Contains(guildChannel.Guild.Id))
            {
                Common.Logger.LogDebug("Ignored message from non-allowed guild {GuildId}", guildChannel.Guild.Id);
                return;
            }

            bool isMentioned = message.MentionedUsers.Any(u => u.Id == client.CurrentUser.Id);
            bool isWatchedChannel = config.WatchedChannelIds.Contains(message.Channel.Id);

            if (!isMentioned && !isWatchedChannel)
            {
                Common.Logger.LogDebug("Ignored server message in non-watched channel {ChannelId} with no mention", message.Channel.Id);
                return;
            }

            conversationKey = $"guild:{guildChannel.Guild.Id}";
            string content = message.Content.Replace($"<@{client.CurrentUser.Id}>", "").Trim();
            contextualPrompt = $"{ServerContextPrompt}\n\n[{message.Author.Username} in #{guildChannel.Name}]: {content}";
        }
        else
        {
            return;
        }

        Common.Logger.LogInformation("Message from {Username} ({UserId}) [{ConversationKey}]: {Content}",
            message.Author.Username, message.Author.Id, conversationKey, message.Content);

        using CancellationTokenSource typingCts = new();
        _ = KeepTypingAsync(message.Channel, typingCts.Token);

        try
        {
            string response = await llmService.Prompt( conversationKey, contextualPrompt);
            typingCts.Cancel();

            if (response.Trim() == PassToken)
            {
                Common.Logger.LogInformation("Ari chose not to respond in [{ConversationKey}]", conversationKey);
                return;
            }

            Common.Logger.LogInformation("ARI reply to {Username} [{ConversationKey}]: {Response}",
                message.Author.Username, conversationKey, response);

            foreach (string chunk in SplitIntoChunks(response))
            {
                await message.Channel.SendMessageAsync(chunk);
                await Task.Delay(MESSAGE_SEND_DELAY_MS);
            }
        }
        catch (LlmRequestFailedException ex)
        {
            typingCts.Cancel();
            Common.Logger.LogError("LLM request failed: {Error}", ex.Message);
            await message.Channel.SendMessageAsync("Ari is unable to respond right now.");
        }
        catch (ModelNotFoundException ex)
        {
            typingCts.Cancel();
            Common.Logger.LogError("Dialogue model not available: {Error}", ex.Message);
            await message.Channel.SendMessageAsync("Ari is unable to respond right now.");
        }
    }

    private static async Task KeepTypingAsync(IMessageChannel channel, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await channel.TriggerTypingAsync();
            try { await Task.Delay(8000, ct); } catch (TaskCanceledException) { break; }
        }
    }

    private static IEnumerable<string> SplitIntoChunks(string text)
    {
        for (int i = 0; i < text.Length; i += MAX_MESSAGE_LENGTH)
            yield return text.Substring(i, Math.Min(MAX_MESSAGE_LENGTH, text.Length - i));
    }

    private Task LogAsync(LogMessage log)
    {
        LogLevel level = log.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error    => LogLevel.Error,
            LogSeverity.Warning  => LogLevel.Warning,
            LogSeverity.Info     => LogLevel.Information,
            LogSeverity.Verbose  => LogLevel.Debug,
            LogSeverity.Debug    => LogLevel.Trace,
            _                    => LogLevel.Information
        };

        Common.Logger.Log(level, log.Exception, "[Discord.Net] {Message}", log.Message);
        return Task.CompletedTask;
    }
}

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
    
    
    private const string PassToken = "[PASS]";

    private const string ServerContextPrompt =
        "You are present in a Discord server. Each message shows who is speaking and in which channel. " +
        "If the conversation was clearly not directed at you and you don't need to be involved, reply with only: [PASS] — nothing else. " +
        "Otherwise, reply normally.";

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

        if (message.Channel is IDMChannel)
            await HandleDMAsync(message);
        else if (message.Channel is SocketGuildChannel guildChannel)
            await HandleServerMessageAsync(message, guildChannel);
    }

    private async Task HandleDMAsync(SocketMessage message)
    {
        if (message.Author.Id != config.OwnerId)
        {
            Common.Logger.LogDebug("Ignored DM from non-owner user {UserId}", message.Author.Id);
            return;
        }

        if (message.Content.StartsWith("/whitelist", StringComparison.OrdinalIgnoreCase))
        {
            await HandleWhitelistCommandAsync(message);
            return;
        }

        string conversationKey = $"dm:{message.Author.Id}";
        string prompt = $"[{message.Author.Username} via DM]: {message.Content}";

        Common.Logger.LogInformation("DM from {Username} ({UserId}): {Content}",
            message.Author.Username, message.Author.Id, message.Content);

        await SendLlmReplyAsync(message, conversationKey, prompt);
    }

    private async Task HandleServerMessageAsync(SocketMessage message, SocketGuildChannel guildChannel)
    {
        if (!config.WhitelistedUserIds.Contains(message.Author.Id))
        {
            Common.Logger.LogDebug("Ignored server message from non-whitelisted user {UserId}", message.Author.Id);
            return;
        }

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

        string conversationKey = $"guild:{guildChannel.Guild.Id}";
        string content = message.Content.Replace($"<@{client.CurrentUser.Id}>", "").Trim();
        string prompt = $"[{message.Author.Username} in #{guildChannel.Name}]: {content}";

        Common.Logger.LogInformation("Server message from {Username} ({UserId}) in #{ChannelName}: {Content}",
            message.Author.Username, message.Author.Id, guildChannel.Name, message.Content);

        await SendLlmReplyAsync(message, conversationKey, prompt, ServerContextPrompt);
    }

    private async Task SendLlmReplyAsync(SocketMessage message, string conversationKey, string prompt, string? contextNote = null)
    {
        using CancellationTokenSource typingCts = new();
        _ = KeepTypingAsync(message.Channel, typingCts.Token);

        try
        {
            string response = await llmService.Prompt(conversationKey, prompt, contextNote);
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

    private async Task HandleWhitelistCommandAsync(SocketMessage message)
    {
        // Expected syntax: /whitelist add/remove <userId or @mention>
        string[] parts = message.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3)
        {
            await message.Channel.SendMessageAsync("Usage: `/whitelist add/remove <user>`");
            return;
        }

        string action = parts[1].ToLowerInvariant();
        if (action != "add" && action != "remove")
        {
            await message.Channel.SendMessageAsync("Unknown action. Use `add` or `remove`.");
            return;
        }

        // Accept a raw ID or a mention (<@userId> or <@!userId>)
        string rawUser = parts[2].Trim('<', '>', '@', '!');
        if (!ulong.TryParse(rawUser, out ulong userId))
        {
            await message.Channel.SendMessageAsync("Could not parse user ID. Provide a user ID or mention.");
            return;
        }

        if (action == "add")
        {
            if (config.WhitelistedUserIds.Contains(userId))
            {
                await message.Channel.SendMessageAsync($"`{userId}` is already whitelisted.");
                return;
            }
            config.WhitelistedUserIds.Add(userId);
            config.Save();
            Common.Logger.LogInformation("Owner added {UserId} to whitelist", userId);
            await message.Channel.SendMessageAsync($"`{userId}` added to whitelist.");
        }
        else
        {
            if (!config.WhitelistedUserIds.Contains(userId))
            {
                await message.Channel.SendMessageAsync($"`{userId}` is not on the whitelist.");
                return;
            }
            config.WhitelistedUserIds.Remove(userId);
            config.Save();
            Common.Logger.LogInformation("Owner removed {UserId} from whitelist", userId);
            await message.Channel.SendMessageAsync($"`{userId}` removed from whitelist.");
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

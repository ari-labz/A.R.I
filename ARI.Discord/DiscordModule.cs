using ARI.Common;
using System.Text;
using ARI.LLM;
using Discord;
using Discord.WebSocket;
using DiscordAttachment = global::Discord.Attachment;
using LlmAttachment = ARI.LLM.Attachment;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ARI.Discord;

public class DiscordModule : BackgroundService, IDiscordModule
{
    private const int MAX_MESSAGE_LENGTH = 2000;
    private const int MESSAGE_SEND_DELAY_MS = 500;

    private static readonly HashSet<string> ReadableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".cs", ".py", ".js", ".ts", ".json", ".yaml", ".yml", ".toml", ".xml", ".html", ".css", ".sh", ".log" };

    private readonly DiscordSocketClient client;
    private readonly DiscordConfig config;
    private readonly LLMModule llmModule;
    private readonly HttpClient httpClient = new();
    private readonly ILogger _logger;
    
    
    private const string PassToken = "[PASS]";

    private const string ServerPlatformContext =
        "You are present in a Discord server. Each message shows who is speaking and in which channel. " +
        "If the conversation was clearly not directed at you and you don't need to be involved, reply with only: [PASS] — nothing else. " +
        "Otherwise, reply normally.";

    public DiscordModule(ILoggerFactory loggerFactory, LLMModule llmModule, DiscordConfig config)
    {
        _logger = loggerFactory.CreateLogger("ARI.Discord");
        _logger.LogInformation("Initialising Discord...");

        this.llmModule = llmModule;
        this.config     = config;

        client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
        });

        client.Log             += LogAsync;
        client.Ready           += OnReady;
        client.MessageReceived += message => { _ = Task.Run(() => OnMessageReceived(message)); return Task.CompletedTask; };
        client.SlashCommandExecuted += cmd => { _ = Task.Run(() => OnSlashCommand(cmd)); return Task.CompletedTask; };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Connecting to Discord...");
        await client.LoginAsync(TokenType.Bot, config.Token);
        await client.StartAsync();

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }


    public async Task NotifyOwner(string message)
    {
        IUser owner = await client.GetUserAsync(config.OwnerId);
        IDMChannel dm = await owner.CreateDMChannelAsync();
        await dm.SendMessageAsync(message);
    }

    public async Task NotifyOffline()
    {
        _logger.LogInformation("Notifying owner that ARI is going offline...");

        IUser owner = await client.GetUserAsync(config.OwnerId);
        IDMChannel dm = await owner.CreateDMChannelAsync();
        await dm.SendMessageAsync(AsBlockQuote("A·R·I is offline."));
    }

    private DateTimeOffset botOnlineSince = DateTimeOffset.UtcNow;

    private async Task OnReady()
    {
        botOnlineSince = DateTimeOffset.UtcNow;
        _logger.LogInformation("Discord bot is ready. Registering slash commands...");
        await RegisterSlashCommands();

        try
        {
            IUser owner = await client.GetUserAsync(config.OwnerId);
            IDMChannel dm = await owner.CreateDMChannelAsync();
            await dm.SendMessageAsync(AsBlockQuote("A·R·I is online."));
            _logger.LogInformation("Sent online notification to owner {OwnerId}", config.OwnerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not send online notification to owner: {Message}", ex.Message);
        }
    }

    private async Task RegisterSlashCommands()
    {
        ApplicationCommandProperties[] commands =
        [
            new SlashCommandBuilder()
                .WithName("engram")
                .WithDescription("Control A·R·I's memory system")
                .AddOption(new SlashCommandOptionBuilder().WithName("on")      .WithDescription("Enable Engram")                    .WithType(ApplicationCommandOptionType.SubCommand))
                .AddOption(new SlashCommandOptionBuilder().WithName("off")     .WithDescription("Disable Engram")                   .WithType(ApplicationCommandOptionType.SubCommand))
                .AddOption(new SlashCommandOptionBuilder().WithName("status")  .WithDescription("Show whether Engram is enabled")   .WithType(ApplicationCommandOptionType.SubCommand))
                .AddOption(new SlashCommandOptionBuilder().WithName("sweep")   .WithDescription("Manually trigger a memory sweep")  .WithType(ApplicationCommandOptionType.SubCommand))
                .Build(),

            new SlashCommandBuilder()
                .WithName("refactor")
                .WithDescription("Rule-based graph analysis — algorithmic fixes + bounded LLM calls")
                .AddOption(new SlashCommandOptionBuilder().WithName("dirty").WithDescription("Process only notes changed since the last refactor (default)").WithType(ApplicationCommandOptionType.SubCommand))
                .AddOption(new SlashCommandOptionBuilder().WithName("all")  .WithDescription("Full scan of every note — use for first run or explicit rebuild") .WithType(ApplicationCommandOptionType.SubCommand))
                .Build(),

            new SlashCommandBuilder()
                .WithName("getdirtynotes")
                .WithDescription("List all notes currently marked dirty (changed since last refactor)")
                .Build(),

            new SlashCommandBuilder()
                .WithName("brain")
                .WithDescription("Brain management")
                .AddOption(new SlashCommandOptionBuilder().WithName("backup").WithDescription("Export the full brain to a backup zip").WithType(ApplicationCommandOptionType.SubCommand))
                .Build(),

            new SlashCommandBuilder()
                .WithName("purge")
                .WithDescription("Destructive brain operations")
                .AddOption(new SlashCommandOptionBuilder().WithName("notes").WithDescription("Delete every note in the brain").WithType(ApplicationCommandOptionType.SubCommand))
                .Build(),

            new SlashCommandBuilder()
                .WithName("whitelist")
                .WithDescription("Manage which users A·R·I responds to in servers")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("add")
                    .WithDescription("Allow a user")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption("user", ApplicationCommandOptionType.User, "The user to allow", isRequired: true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("remove")
                    .WithDescription("Remove a user")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption("user", ApplicationCommandOptionType.User, "The user to remove", isRequired: true))
                .Build(),
        ];

        try
        {
            // Always register globally so commands work in DMs (guild commands never appear in DMs).
            // Global commands take up to 1 hour to propagate on first registration, but updates
            // to existing global commands are usually near-instant.
            await client.Rest.BulkOverwriteGlobalCommands(commands);
            _logger.LogInformation("Slash commands registered globally (available in DMs and servers).");

            // Also register per-guild for instant availability in known servers.
            foreach (ulong guildId in config.AllowedGuildIds)
            {
                await client.Rest.BulkOverwriteGuildCommands(commands, guildId);
                _logger.LogInformation("Slash commands registered for guild {GuildId}", guildId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to register slash commands: {Error}", ex.Message);
        }
    }

    private async Task OnSlashCommand(SocketSlashCommand cmd)
    {
        // Only the owner may use ARI's commands.
        if (cmd.User.Id != config.OwnerId)
        {
            await cmd.RespondAsync("You are not authorised to use A·R·I's commands.", ephemeral: true);
            return;
        }

        string sub = cmd.Data.Options.FirstOrDefault()?.Name ?? string.Empty;

        // whitelist is Discord-specific — it manages Discord user IDs directly.
        if (cmd.CommandName == "whitelist")
        {
            await HandleWhitelistSlash(cmd, sub);
            return;
        }

        // All other commands: reconstruct the text form and route through CommandService.
        string commandText = string.IsNullOrEmpty(sub)
            ? $"/{cmd.CommandName}"
            : $"/{cmd.CommandName} {sub}";

        // Defer immediately — sweep/refactor/backup can take well over 3 seconds.
        await cmd.DeferAsync(ephemeral: true);
        string result = await llmModule.HandleCommand(null, commandText) ?? $"Unknown command: {commandText}";
        await cmd.FollowupAsync(AsBlockQuote(result), ephemeral: true);
    }

    private async Task HandleWhitelistSlash(SocketSlashCommand cmd, string sub)
    {
        SocketSlashCommandDataOption? subCmd = cmd.Data.Options.FirstOrDefault();
        IUser? target = subCmd?.Options.FirstOrDefault()?.Value as IUser;

        if (target is null)
        {
            await cmd.RespondAsync("Could not resolve user.", ephemeral: true);
            return;
        }

        ulong userId = target.Id;

        if (sub == "add")
        {
            if (config.WhitelistedUserIds.Contains(userId))
            {
                await cmd.RespondAsync($"`{target.Username}` is already whitelisted.", ephemeral: true);
                return;
            }
            config.WhitelistedUserIds.Add(userId);
            _logger.LogInformation("Owner added {UserId} to whitelist", userId);
            await cmd.RespondAsync($"`{target.Username}` added to whitelist.", ephemeral: true);
        }
        else
        {
            if (!config.WhitelistedUserIds.Contains(userId))
            {
                await cmd.RespondAsync($"`{target.Username}` is not on the whitelist.", ephemeral: true);
                return;
            }
            config.WhitelistedUserIds.Remove(userId);
            _logger.LogInformation("Owner removed {UserId} from whitelist", userId);
            await cmd.RespondAsync($"`{target.Username}` removed from whitelist.", ephemeral: true);
        }
    }

    private async Task OnMessageReceived(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        if (client.CurrentUser is not null && message.Author.Id == client.CurrentUser.Id) return;
        // Discard messages sent before the bot came online — Discord.Net replays missed messages on reconnect
        if (message.Timestamp < botOnlineSince)
        {
            _logger.LogDebug("Discarding stale message from {Username} sent at {Timestamp} (bot online since {OnlineSince})",
                message.Author.Username, message.Timestamp, botOnlineSince);
            return;
        }

        if (message.Channel is IDMChannel)
            await HandleDM(message);
        else if (message.Channel is SocketGuildChannel guildChannel)
            await HandleServerMessage(message, guildChannel);
    }

    private async Task HandleDM(SocketMessage message)
    {
        if (message.Author.Id != config.OwnerId)
        {
            _logger.LogDebug("Ignored DM from non-owner user {UserId}", message.Author.Id);
            return;
        }

        // Slash commands are the primary path (OnSlashCommand).
        // Plain-text /commands in DMs are kept as a fallback — useful before global slash
        // commands have propagated, or if the interaction system fails.
        if (message.Content.StartsWith("/", StringComparison.OrdinalIgnoreCase))
        {
            string? result = await llmModule.HandleCommand(null, message.Content);
            if (result is not null)
            {
                _logger.LogInformation("Command [{Input}] → {Result}", message.Content, result);
                await message.Channel.SendMessageAsync(AsBlockQuote(result));
            }
            return;
        }

        string conversationKey = $"dm:{message.Author.Id}";
        string timestamp = message.Timestamp.LocalDateTime.ToString("dd/MM/yyyy HH:mm");
        string prompt = $"[{timestamp}] [{message.Author.Username} via DM]: {message.Content}";

        _logger.LogInformation("DM from {Username} ({UserId}): {Content}",
            message.Author.Username, message.Author.Id, message.Content);

        List<LlmAttachment>? attachments = message.Attachments.Count > 0
            ? await UploadDiscordAttachments(message.Attachments)
            : null;
        await SendLlmReply(message, conversationKey, prompt, message.Author.Username, attachments: attachments);
    }

    private async Task HandleServerMessage(SocketMessage message, SocketGuildChannel guildChannel)
    {
        if (!config.WhitelistedUserIds.Contains(message.Author.Id))
        {
            _logger.LogDebug("Ignored server message from non-whitelisted user {UserId}", message.Author.Id);
            return;
        }

        if (config.AllowedGuildIds.Count > 0 && !config.AllowedGuildIds.Contains(guildChannel.Guild.Id))
        {
            _logger.LogDebug("Ignored message from non-allowed guild {GuildId}", guildChannel.Guild.Id);
            return;
        }

        bool isMentioned = message.MentionedUsers.Any(u => u.Id == client.CurrentUser.Id);
        bool isWatchedChannel = config.WatchedChannelIds.Contains(message.Channel.Id);

        if (!isMentioned && !isWatchedChannel)
        {
            _logger.LogDebug("Ignored server message in non-watched channel {ChannelId} with no mention", message.Channel.Id);
            return;
        }

        string conversationKey = $"guild:{guildChannel.Guild.Id}";
        string content = message.Content.Replace($"<@{client.CurrentUser.Id}>", "").Trim();
        string timestamp = message.Timestamp.LocalDateTime.ToString("dd/MM/yyyy HH:mm");
        string prompt = $"[{timestamp}] [{message.Author.Username} in #{guildChannel.Name}]: {content}";

        _logger.LogInformation("Server message from {Username} ({UserId}) in #{ChannelName}: {Content}",
            message.Author.Username, message.Author.Id, guildChannel.Name, message.Content);

        List<LlmAttachment>? attachments = message.Attachments.Count > 0
            ? await UploadDiscordAttachments(message.Attachments)
            : null;
        await SendLlmReply(message, conversationKey, prompt, message.Author.Username, ServerPlatformContext, attachments);
    }

    private async Task<List<LlmAttachment>> UploadDiscordAttachments(IReadOnlyCollection<DiscordAttachment> attachments)
    {
        List<LlmAttachment> result = new();
        foreach (DiscordAttachment att in attachments)
        {
            string mime    = att.ContentType ?? "application/octet-stream";
            bool   isImage = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            string ext     = Path.GetExtension(att.Filename);

            if (!isImage && !ReadableExtensions.Contains(ext))
            {
                _logger.LogDebug("Skipping unsupported Discord attachment: {Filename}", att.Filename);
                continue;
            }

            byte[] bytes   = await httpClient.GetByteArrayAsync(att.Url);
            string content = isImage ? Convert.ToBase64String(bytes) : Encoding.UTF8.GetString(bytes);

            result.Add(new LlmAttachment { Name = att.Filename, Content = content, IsImage = isImage, MimeType = mime });
            _logger.LogDebug("Loaded Discord attachment: {Filename} ({Mime})", att.Filename, mime);
        }
        return result;
    }

    private async Task SendLlmReply(SocketMessage message, string conversationKey, string prompt, string username, string? platformContext = null, List<LlmAttachment>? attachments = null)
    {
        using CancellationTokenSource typingCts = new();
        _ = KeepTyping(message.Channel, typingCts.Token);

        try
        {
            string response = await llmModule.Prompt(conversationKey, prompt, username, platformContext, messageAttachments: attachments);
            typingCts.Cancel();

            if (response.Trim() == PassToken)
            {
                _logger.LogInformation("Ari chose not to respond in [{ConversationKey}]", conversationKey);
                return;
            }

            _logger.LogInformation("ARI reply sent to {Username} [{ConversationKey}]",
                message.Author.Username, conversationKey);

            foreach (string chunk in SplitIntoChunks(response))
            {
                await message.Channel.SendMessageAsync(chunk);
                await Task.Delay(MESSAGE_SEND_DELAY_MS);
            }
        }
        catch (LlmRequestFailedException ex)
        {
            typingCts.Cancel();
            _logger.LogError("LLM request failed: {Error}", ex.Message);
            await message.Channel.SendMessageAsync(AsBlockQuote("A·R·I is unable to respond right now."));
        }
        catch (ModelNotFoundException ex)
        {
            typingCts.Cancel();
            _logger.LogError("Dialogue model not available: {Error}", ex.Message);
            await message.Channel.SendMessageAsync(AsBlockQuote("A·R·I is unable to respond right now."));
        }
    }

    private static async Task KeepTyping(IMessageChannel channel, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await channel.TriggerTypingAsync();
            try { await Task.Delay(8000, ct); } catch (TaskCanceledException) { break; }
        }
    }

    private static string AsBlockQuote(string text) =>
        string.Join('\n', text.Split('\n').Select(line => $"> {line}"));

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

        _logger.Log(level, log.Exception, "[Discord.Net] {Message}", log.Message);
        return Task.CompletedTask;
    }
}

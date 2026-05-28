using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ARI.Discord;

public class DiscordService : BackgroundService
{
    private readonly DiscordSocketClient client;
    private readonly DiscordConfig config;

    public DiscordService(ILoggerFactory loggerFactory)
    {
        Common.InitialiseLogger(loggerFactory);
        Common.Logger.LogInformation("Initialising Discord...");
        
        string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        config = DiscordConfig.LoadFrom(Path.Combine(executableDirectory, "DiscordConfig.json"));
        

        client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.DirectMessages | GatewayIntents.MessageContent
        });

        client.Log += LogAsync;
        client.Ready += OnReadyAsync;
        client.MessageReceived += OnMessageReceivedAsync;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Common.Logger.LogInformation("Connecting to Discord...");
        await client.LoginAsync(TokenType.Bot, config.Token);
        await client.StartAsync();

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task OnReadyAsync()
    {
        Common.Logger.LogInformation("Discord bot is ready. Notifying whitelisted users...");

        foreach (ulong userId in config.WhitelistedUserIds)
        {
            IUser user = await client.GetUserAsync(userId);
            IDMChannel dm = await user.CreateDMChannelAsync();
            await dm.SendMessageAsync("Ari is online.");
            Common.Logger.LogInformation("Sent online notification to user {UserId}", userId);
        }
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;

        bool isWhitelisted = config.WhitelistedUserIds.Contains(message.Author.Id);

        if (!isWhitelisted)
        {
            Common.Logger.LogDebug("Ignored message from non-whitelisted user {UserId}", message.Author.Id);
            return;
        }

        Common.Logger.LogInformation("Message from {Username} ({UserId}): {Content}",
            message.Author.Username, message.Author.Id, message.Content);

        await message.Channel.SendMessageAsync("Ari received your message.");
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
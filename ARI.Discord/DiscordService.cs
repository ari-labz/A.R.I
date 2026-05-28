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
            GatewayIntents = GatewayIntents.DirectMessages | GatewayIntents.MessageContent
        });

        client.Log += LogAsync;
        client.Ready += OnReadyAsync;
        // Task.Run frees the gateway thread immediately. If multiple whitelisted users are ever
        // added, concurrent LLM calls will race — a per-user queue will be needed at that point.
        client.MessageReceived += message => { _ = Task.Run(() => OnMessageReceivedAsync(message)); return Task.CompletedTask; };
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

        await message.Channel.TriggerTypingAsync();

        try
        {
            string response = await llmService.PromptModel("Dialogue", message.Content);

            foreach (string chunk in SplitIntoChunks(response))
            {
                await message.Channel.SendMessageAsync(chunk);
                await Task.Delay(MESSAGE_SEND_DELAY_MS);
            }
        }
        catch (LlmRequestFailedException ex)
        {
            Common.Logger.LogError("LLM request failed: {Error}", ex.Message);
            await message.Channel.SendMessageAsync("Ari is unable to respond right now.");
        }
        catch (ModelNotFoundException ex)
        {
            Common.Logger.LogError("Dialogue model not available: {Error}", ex.Message);
            await message.Channel.SendMessageAsync("Ari is unable to respond right now.");
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

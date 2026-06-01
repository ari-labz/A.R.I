using ARI.Core.Scripts;
using ARI.Discord;
using ARI.LLM;
using ARI.WebPanel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Common = ARI.Core.Scripts.Common;

namespace ARI.Core;

public class AriHostService : BackgroundService
{
    private readonly ILoggerFactory loggerFactory;
    private Docker? docker;
    private LocalLlamaServer? llamaServer;
    private DiscordService? discordService;
    private WebPanelService? webPanelService;
    private bool containersStarted;
    private bool startupFailed;

    public AriHostService(ILoggerFactory loggerFactory)
    {
        this.loggerFactory = loggerFactory;
        Common.InitialiseLogger(loggerFactory);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await StartAsync(stoppingToken);
        }
        catch
        {
            startupFailed = true;
            throw;
        }
    }

    private async Task StartAsync(CancellationToken stoppingToken)
    {
        Common.Logger.LogInformation("ARI is starting...");

        string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        AriConfig config = AriConfig.LoadFrom(Path.Combine(executableDirectory, "AriConfig.json"));

        // Start web panel immediately so the browser never gets "connection refused"
        // It serves 503 until LlmService is ready later in startup
        if (config.Modules.WebPanel)
        {
            Common.Logger.LogInformation("Web panel module is enabled. Starting on port {Port}...", config.WebPanel.Port);
            webPanelService = new WebPanelService(loggerFactory, new ARI.WebPanel.WebPanelConfig
            {
                Port              = config.WebPanel.Port,
                GoogleClientId    = config.WebPanel.Google.ClientId,
                GoogleClientSecret= config.WebPanel.Google.ClientSecret,
                AllowedEmail      = config.WebPanel.Google.AllowedEmail,
            });
            await webPanelService.StartAsync(stoppingToken);
        }

        string fullComposePath = Path.Combine(executableDirectory, config.Docker.ComposePath);
        docker = new Docker(fullComposePath);

        await Dependency.CheckDocker();
        await Dependency.CheckPython();

        await docker.IsRunning();
        await docker.StartContainers();
        containersStarted = true;

        llamaServer = new LocalLlamaServer(config.LlamaServer, executableDirectory);
        await llamaServer.IsReady();

        Common.Logger.LogInformation("Loading LLM models...");
        string brainConfigPath = Path.Combine(executableDirectory, "AriBrain.json");
        LlmService llmService = new LlmService(
            Path.Combine(executableDirectory, "AriModels.json"),
            File.Exists(brainConfigPath) ? brainConfigPath : null,
            loggerFactory
        );
        Common.Logger.LogInformation("LLM models loaded.");

        Common.Logger.LogInformation("ARI is ready.");

        // Give the web panel the LlmService now that it's ready
        webPanelService?.Holder.Set(llmService);

        List<Task> moduleTasks = new();

        if (config.Modules.Discord)
        {
            Common.Logger.LogInformation("Discord module is enabled. Starting...");
            discordService = new DiscordService(loggerFactory, llmService);
            await discordService.StartAsync(stoppingToken);
            if (discordService.ExecuteTask is not null)
                moduleTasks.Add(discordService.ExecuteTask);
        }

        if (moduleTasks.Count > 0)
            await Task.WhenAny(moduleTasks.Concat(new[] { Task.Delay(Timeout.Infinite, stoppingToken) }));
        else
            await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (startupFailed) return;

        Common.Logger.LogInformation("ARI is shutting down...");

        if (discordService != null)
            await discordService.NotifyOfflineAsync();

        if (webPanelService is not null)
            await webPanelService.StopAsync(cancellationToken);

        llamaServer?.Stop();

        if (docker != null && containersStarted)
            await docker.StopContainers();

        await base.StopAsync(cancellationToken);
    }
}

using ARI.Core.Scripts;
using ARI.Discord;
using ARI.LLM;
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

        if (config.Modules.Discord)
        {
            Common.Logger.LogInformation("Discord module is enabled. Starting...");
            discordService = new DiscordService(loggerFactory, llmService);
            await discordService.StartAsync(stoppingToken);

            await Task.WhenAny(discordService.ExecuteTask ?? Task.CompletedTask, Task.Delay(Timeout.Infinite, stoppingToken));
        }

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (startupFailed) return;

        Common.Logger.LogInformation("ARI is shutting down...");

        if (discordService != null)
            await discordService.NotifyOfflineAsync();

        llamaServer?.Stop();

        if (docker != null && containersStarted)
            await docker.StopContainers();

        await base.StopAsync(cancellationToken);
    }
}

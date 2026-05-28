using ARI.Core.LLM;
using ARI.Core.Scripts;
using ARI.Discord;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Common = ARI.Core.Scripts.Common;

namespace ARI.Core;

public class AriHostService : BackgroundService
{
    private readonly ILoggerFactory loggerFactory;
    private Docker? docker;

    public AriHostService(ILoggerFactory loggerFactory)
    {
        this.loggerFactory = loggerFactory;
        Common.InitialiseLogger(loggerFactory);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Common.Logger.LogInformation("ARI is starting...");

        string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        AriConfig config = AriConfig.LoadFrom(Path.Combine(executableDirectory, "AriConfig.json"));

        string fullComposePath = Path.Combine(executableDirectory, config.Docker.ComposePath);
        docker = new Docker(fullComposePath, config.LLM.Endpoint);

        await Dependency.CheckDocker();
        await Dependency.CheckPython();

        await docker.IsRunning();
        await docker.StartContainers();

        Ollama ollama = new Ollama(config.LLM.Endpoint, config.LLM.Model, docker.OllamaContainerName);
        await ollama.IsRunning();
        await ollama.IsModelInstalled();

        Common.Logger.LogInformation("ARI is ready.");

        
        if (config.Modules.Discord)
        {
            Common.Logger.LogInformation("Discord module is enabled. Starting...");
            DiscordService discordService = new DiscordService(loggerFactory);
            await discordService.StartAsync(stoppingToken);

            // Await the running task directly so exceptions surface to the debugger
            await Task.WhenAny(discordService.ExecuteTask ?? Task.CompletedTask, Task.Delay(Timeout.Infinite, stoppingToken));
        }

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Common.Logger.LogInformation("ARI is shutting down...");

        if (docker != null)
            await docker.StopContainers();

        await base.StopAsync(cancellationToken);
    }
}
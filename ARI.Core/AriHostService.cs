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
    private DiscordService? discordService;

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

        Ollama ollama = new Ollama(config.LLM.Endpoint, docker.OllamaContainerName);
        await ollama.IsRunning();

        Common.Logger.LogInformation("Loading LLM models...");
        LlmService llmService = new LlmService(Path.Combine(executableDirectory, "AriModels.json"));
        Common.Logger.LogInformation("LLM models loaded.");

        foreach (string ollamaModel in llmService.OllamaModelNames)
            await ollama.IsModelInstalled(ollamaModel);

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
        Common.Logger.LogInformation("ARI is shutting down...");

        if (discordService != null)
            await discordService.NotifyOfflineAsync();

        if (docker != null)
            await docker.StopContainers();

        await base.StopAsync(cancellationToken);
    }
}

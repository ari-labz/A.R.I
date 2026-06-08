using ARI.Core.Scripts;
using ARI.Discord;
using ARI.LLM;
using ARI.VoiceSynthesis;
using ARI.WebPanel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Common = ARI.Core.Scripts.Common;

namespace ARI.Core;

public class AriHostService : BackgroundService
{
    private readonly ILoggerFactory loggerFactory;
    private Docker? docker;
    private readonly List<LocalLlamaServer> llamaServers = new();
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
            await Start(stoppingToken);
        }
        catch (Exception ex)
        {
            startupFailed = true;
            Common.Logger.LogCritical("Startup failed: {Error}", ex.Message);
            throw;
        }
    }

    private async Task Start(CancellationToken stoppingToken)
    {
        Common.Logger.LogInformation("ARI is starting...");

        string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        AriConfig config = AriConfig.LoadFrom(Path.Combine(executableDirectory, "AriConfig.json"));

        if (config.Modules.WebPanel)
        {
            Common.Logger.LogInformation("Web panel module is enabled. Starting on port {Port}...", config.WebPanel.Port);

            string f5Path     = ResolvePath(executableDirectory, config.Modules.VoiceSynthesis ? config.VoiceSynthesis.F5Path : "");
            string voicesPath = ResolvePath(executableDirectory, config.Modules.VoiceSynthesis ? config.VoiceSynthesis.VoicesPath : "");

            webPanelService = new WebPanelService(loggerFactory, new ARI.WebPanel.WebPanelConfig
            {
                Port               = config.WebPanel.Port,
                GoogleClientId     = config.WebPanel.Google.ClientId,
                GoogleClientSecret = config.WebPanel.Google.ClientSecret,
                AllowedEmail       = config.WebPanel.Google.AllowedEmail,
                LogPath            = Path.Combine(executableDirectory, "ARI.log"),
                F5Path             = f5Path,
                VoicesPath         = voicesPath,
            });
            await webPanelService.Start(stoppingToken);
        }

        string fullComposePath = Path.Combine(executableDirectory, config.Docker.ComposePath);
        docker = new Docker(fullComposePath);

        await Dependency.CheckDocker();
        await Dependency.CheckPython();

        await docker.IsRunning();
        await docker.StartContainers();
        containersStarted = true;

        AriLLMConfig llmConfig = AriLLMConfig.LoadFrom(Path.Combine(executableDirectory, "AriLLMConfig.json"));
        foreach (LlamaModelConfig modelConfig in llmConfig.Models)
        {
            LlamaServerConfig serverConfig = llmConfig.Servers[modelConfig.ServerIndex];
            LocalLlamaServer  llamaServer  = new(serverConfig, modelConfig, executableDirectory);
            await llamaServer.IsReady();
            llamaServers.Add(llamaServer);
        }
        if (llamaServers.Count > 0)
            webPanelService?.SystemInfo.SetLlamaPid(llamaServers[0].Pid);

        Common.Logger.LogInformation("Loading agents...");
        string brainConfigPath = Path.Combine(executableDirectory, "AriBrain.json");
        LlmService llmService = new LlmService(
            Path.Combine(executableDirectory, "AriAgents.json"),
            File.Exists(brainConfigPath) ? brainConfigPath : null,
            loggerFactory
        );
        Common.Logger.LogInformation("Agents loaded.");

        Common.Logger.LogInformation("ARI is ready.");

        webPanelService?.Holder.Set(llmService);

        List<Task> moduleTasks = new();

        if (config.Modules.VoiceSynthesis)
        {
            Common.Logger.LogInformation("VoiceSynthesis module is enabled. Installing F5-TTS...");
            string f5Path = ResolvePath(executableDirectory, config.VoiceSynthesis.F5Path);
            string voicesPath = ResolvePath(executableDirectory, config.VoiceSynthesis.VoicesPath);
            F5SetupService setup = new(f5Path, loggerFactory.CreateLogger("ARI.VoiceSynthesis"));
            await setup.Install();
        }

        if (config.Modules.Discord)
        {
            Common.Logger.LogInformation("Discord module is enabled. Starting...");
            discordService = new DiscordService(loggerFactory, llmService);
            await discordService.StartAsync(stoppingToken);
            if (discordService.ExecuteTask is not null)
                moduleTasks.Add(discordService.ExecuteTask);

            webPanelService?.DiscordHolder.Set(msg => discordService.NotifyOwner(msg));
        }

        if (moduleTasks.Count > 0)
            await Task.WhenAny(moduleTasks.Concat(new[] { Task.Delay(Timeout.Infinite, stoppingToken) }));
        else
            await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private static string ResolvePath(string baseDir, string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDir, path));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (startupFailed) return;

        Common.Logger.LogInformation("ARI is shutting down...");

        if (discordService != null)
            await discordService.NotifyOffline();

        if (webPanelService is not null)
            await webPanelService.Stop(cancellationToken);

        foreach (LocalLlamaServer s in llamaServers)
            s.Stop();

        if (docker != null && containersStarted)
            await docker.StopContainers();

        await base.StopAsync(cancellationToken);
    }
}

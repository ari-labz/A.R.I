using ARI.Core.Scripts;
using ARI.Discord;
using ARI.LLM;
using ARI.Voice;
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
    private F5Synthesiser? synthesiser;
    private SpeechQueue? speechQueue;
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
                AllowedEmails      = config.WebPanel.Google.EffectiveAllowedEmails.ToList(),
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

        if (config.Modules.Voice)
        {
            string f5Path     = ResolvePath(executableDirectory, config.VoiceSynthesis.F5Path);
            string voicesPath = ResolvePath(executableDirectory, config.VoiceSynthesis.VoicesPath);
            string modelName  = config.Voice.ModelName;
            string modelPath  = Path.Combine(voicesPath, modelName, "model_last.pt");
            string refAudio   = FindReferenceAudio(f5Path, modelName);

            if (!File.Exists(modelPath))
            {
                Common.Logger.LogWarning("Voice module enabled but no model found at {Path} — skipping.", modelPath);
            }
            else if (string.IsNullOrEmpty(refAudio))
            {
                Common.Logger.LogWarning("Voice module enabled but no reference audio found for {Model} — skipping.", modelName);
            }
            else
            {
                ILogger voiceLogger = loggerFactory.CreateLogger("ARI.Voice");
                synthesiser = new F5Synthesiser(f5Path, modelPath, refAudio, voiceLogger);
                await synthesiser.Start(stoppingToken);

                Common.Logger.LogInformation("Voice warming up (caching reference audio)...");
                await synthesiser.Warmup(stoppingToken);

                speechQueue = new SpeechQueue(synthesiser, voiceLogger);
                string pythonPath = Path.Combine(f5Path, "venv", "bin", "python");
                speechQueue.AudioReady += wav => PlayAudio(wav, pythonPath, voiceLogger);

                webPanelService?.SpeechHolder.Set(synthesiser, speechQueue, modelName);
                Common.Logger.LogInformation("Voice ready.");
            }
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

    private static string FindReferenceAudio(string f5Path, string modelName)
    {
        string audioDir = Path.Combine(f5Path, "training", modelName, "audio");
        if (!Directory.Exists(audioDir)) return "";
        string[] wavs = Directory.GetFiles(audioDir, "*.wav");
        return wavs.Length > 0 ? wavs.OrderBy(f => f).First() : "";
    }

    private static void PlayAudio(byte[] wav, string python, ILogger logger)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ari_speech_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(tmp, wav);

        string script =
            "import sys, sounddevice as sd, soundfile as sf\n" +
            $"data, sr = sf.read(r'{tmp}')\n" +
            "sd.play(data, sr, blocking=True)\n" +
            $"import os; os.remove(r'{tmp}')\n";

        string scriptPath = Path.Combine(Path.GetTempPath(), $"ari_play_{Guid.NewGuid():N}.py");
        File.WriteAllText(scriptPath, script);

        System.Diagnostics.Process? proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName              = python,
            Arguments             = $"\"{scriptPath}\"",
            UseShellExecute       = false,
            RedirectStandardError = true,
        });

        if (proc == null) { logger.LogError("[Voice] Failed to start audio playback"); return; }

        _ = Task.Run(async () =>
        {
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
                logger.LogError("[Voice] Playback failed: {Error}", stderr);
            try { File.Delete(scriptPath); } catch { }
        });
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

        speechQueue?.Dispose();
        synthesiser?.Dispose();

        if (webPanelService is not null)
            await webPanelService.Stop(cancellationToken);

        foreach (LocalLlamaServer s in llamaServers)
            s.Stop();

        if (docker != null && containersStarted)
            await docker.StopContainers();

        await base.StopAsync(cancellationToken);
    }
}

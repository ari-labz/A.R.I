using ARI.Common;
using CommonModules = ARI.Common.Modules;
using ARI.Core.Scripts;
using ARI.Discord;
using ARI.LLM;
using ARI.Voice;
using ARI.VoiceSynthesis;
using ARI.API;
using ARI.API.Data;
using ARI.Brain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ARI.Core;

public class ARI : BackgroundService
{
    public static ARI instance;

    private AriConfig config;

    // modules
    public DiscordModule? discordService;
    public APIModule?     apiModule;
    public BrainModule?   brainModule;
    public VoiceModule?           voiceModule;
    public VoiceSynthesisModule?  voiceSynthesisModule;
    private LLMModule?           llmModule;

    private readonly ILoggerFactory loggerFactory;
    private Docker?              docker;
    private StyleTtsSynthesiser? synthesiser;
    private SpeechQueue?         speechQueue;
    private bool startupFailed;
    private static System.Diagnostics.Process? clientProcess;

    public ARI(ILoggerFactory loggerFactory)
    {
        this.loggerFactory = loggerFactory;
        Shared.InitialiseLogger(loggerFactory, "ARI.Core");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try   { await Startup(stoppingToken); }
        catch (Exception ex)
        {
            startupFailed = true;
            Shared.Logger.LogCritical("Startup failed: {Error}", ex.Message);
            throw;
        }
    }

    private async Task Startup(CancellationToken stoppingToken)
    {
        Shared.Logger.LogInformation("ARI is starting...");

        string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        config = AriConfig.LoadFrom(Path.Combine(executableDirectory, "AriConfig.json"));

        // Resolve relative paths to absolute up front so all modules see consistent paths.
        if (!string.IsNullOrEmpty(config.modules.VoiceSynthesis.StyleTtsPath))
            config.modules.VoiceSynthesis.StyleTtsPath = ResolvePath(executableDirectory, config.modules.VoiceSynthesis.StyleTtsPath);
        if (!string.IsNullOrEmpty(config.modules.VoiceSynthesis.VoicesPath))
            config.modules.VoiceSynthesis.VoicesPath = ResolvePath(executableDirectory, config.modules.VoiceSynthesis.VoicesPath);

        await Dependency.CheckPython();
        await Dependency.CheckDocker();
        await Dependency.CheckHomebrew();
        await Dependency.CheckLlamaCpp();

        docker = new Docker(Path.Combine(executableDirectory, config.DockerComposePath));
        await docker.IsRunning();
        await docker.StartContainers();

        // ── Shared infrastructure ────────────────────────────────────────────────
        PersistentData persistentData = new();

        string ariPersistentDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ari", "Server", "PersistentData");
        string persistentAgentsPath = Path.Combine(ariPersistentDir, "Agents.json");
        persistentData.EnsureAgentsFileFromFallback(Path.Combine(executableDirectory, "Agents.json"));

        // ── LLM module ───────────────────────────────────────────────────────────
        string modelsPath = ResolvePath(executableDirectory, config.modules.LLM.ModelsPath);

        if (config.modules.LLM.Enabled)
        {
            BrainConfig? brainConfig = config.modules.Brain?.Enabled == true ? config.modules.Brain : null;

            Shared.Logger.LogInformation("Loading agents...");
            llmModule = new LLMModule(
                servers:        persistentData.GetServers().ToList(),
                agentsJsonPath: persistentAgentsPath,
                brainConfig:    brainConfig,
                loggerFactory:  loggerFactory);
            CommonModules.Register(llm: llmModule);
            Shared.Logger.LogInformation("Agents loaded.");
        }

        // ── Voice setup ──────────────────────────────────────────────────────────

        voiceSynthesisModule = new VoiceSynthesisModule();
        CommonModules.Register(voiceSynthesis: voiceSynthesisModule);

        if (config.modules.VoiceSynthesis.Enabled)
        {
            Shared.Logger.LogInformation("VoiceSynthesis module is enabled. Installing StyleTTS2...");
            string sttPath = ResolvePath(executableDirectory, config.modules.VoiceSynthesis.StyleTtsPath);
            await new StyleTtsSetupService(sttPath, loggerFactory.CreateLogger("ARI.VoiceSynthesis")).Install();
            voiceSynthesisModule.MarkSetupComplete();
            Shared.Logger.LogInformation("VoiceSynthesis ready.");
        }

        if (config.modules.Voice.Enabled)
        {
            string sttPath    = ResolvePath(executableDirectory, config.modules.VoiceSynthesis.StyleTtsPath);
            string voicesPath = ResolvePath(executableDirectory, config.modules.VoiceSynthesis.VoicesPath);
            string modelName  = config.modules.Voice.ModelName;
            string modelDir   = Path.Combine(voicesPath, modelName);
            string modelPath  = Path.Combine(modelDir, "model.pth");
            string configPath = Path.Combine(modelDir, "config.yml");
            string refAudio   = FindReferenceAudio(modelDir, sttPath, modelName);

            if (!File.Exists(modelPath) || !File.Exists(configPath))
                Shared.Logger.LogWarning("Voice module enabled but no model found at {Path} — skipping.", modelDir);
            else if (string.IsNullOrEmpty(refAudio))
                Shared.Logger.LogWarning("Voice module enabled but no reference audio found for {Model} — skipping.", modelName);
            else
            {
                ILogger voiceLogger = loggerFactory.CreateLogger("ARI.Voice");
                Shared.Logger.LogInformation("Voice loading model: {Model}", modelName);
                synthesiser = new StyleTtsSynthesiser(sttPath, modelPath, configPath, refAudio, voiceLogger);
                await synthesiser.Start(stoppingToken);
                try { await synthesiser.Warmup(stoppingToken); }
                catch (Exception ex) { Shared.Logger.LogError("Voice warmup failed (model may have corrupt weights): {Error}", ex.Message); }

                speechQueue = new SpeechQueue(synthesiser, voiceLogger);
                string pythonPath = Path.Combine(sttPath, OperatingSystem.IsWindows() ? @"venv\Scripts\python.exe" : "venv/bin/python");
                speechQueue.AudioReady += wav => PlayAudio(wav, pythonPath, voiceLogger);

                voiceModule = new VoiceModule(synthesiser, speechQueue, modelName);
                CommonModules.Register(voice: voiceModule);
                Shared.Logger.LogInformation("Voice ready.");
            }
        }

        // ── API ──────────────────────────────────────────────────────────────────
        if (config.modules.API.Enabled)
        {
            Shared.Logger.LogInformation("Web panel module is enabled. Starting on port {Port}...", config.modules.API.Port);

            apiModule = new APIModule(
                loggerFactory:        loggerFactory,
                config:               config.modules.API,
                voiceSynthesisConfig: config.modules.VoiceSynthesis,
                modelsPath:           modelsPath,
                persistentData:       persistentData);

            await apiModule.Start(stoppingToken);
        }

        // ── Start LLM servers (after API is up so status is visible) ────────────
        if (llmModule is not null)
        {
            Shared.Logger.LogInformation("Starting LLM servers...");
            await llmModule.StartServersAsync(persistentData.GetModels().ToList(), modelsPath);

            foreach (var agent in persistentData.GetAgents())
            {
                llmModule.AssignAgentServer(agent.Name, agent.ServerName);
                if (agent.Slot.HasValue) llmModule.AssignAgentSlot(agent.Name, agent.Slot.Value);
            }
        }

        // ── Discord ──────────────────────────────────────────────────────────────
        List<Task> moduleTasks = new();

        if (config.modules.Discord.Enabled)
        {
            Shared.Logger.LogInformation("Discord module is enabled. Starting...");
            discordService = new DiscordModule(loggerFactory, llmModule, config.modules.Discord);
            await discordService.StartAsync(stoppingToken);
            if (discordService.ExecuteTask is not null)
                moduleTasks.Add(discordService.ExecuteTask);

            CommonModules.Register(discord: discordService);
        }

        if (config.modules.API.Enabled)
            LaunchClient(executableDirectory, config.modules.API.Port);

        Shared.Logger.LogInformation("ARI is ready.");

        if (moduleTasks.Count > 0)
            await Task.WhenAny(moduleTasks.Concat(new[] { Task.Delay(Timeout.Infinite, stoppingToken) }));
        else
            await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private static string FindReferenceAudio(string modelDir, string styleTtsPath, string modelName)
    {
        string bundled = Path.Combine(modelDir, "reference.wav");
        if (File.Exists(bundled)) return bundled;

        string audioDir = Path.Combine(styleTtsPath, "Data", modelName, "wavs");
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
            if (proc.ExitCode != 0) logger.LogError("[Voice] Playback failed: {Error}", stderr);
            try { File.Delete(scriptPath); } catch { }
        });
    }

    private static void LaunchClient(string executableDirectory, int port)
    {
        string? scriptPath = null;
        DirectoryInfo? dir = new DirectoryInfo(executableDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "ARI.Client", "setup.sh");
            if (File.Exists(candidate)) { scriptPath = candidate; break; }
            dir = dir.Parent;
        }

        if (scriptPath is null)
        {
            Shared.Logger.LogWarning("[Client] setup.sh not found — skipping client launch.");
            return;
        }

        try
        {
            if (clientProcess is not null && !clientProcess.HasExited)
            {
                Shared.Logger.LogInformation("[Client] Stopping previous client instance (PID {Pid})...", clientProcess.Id);
                clientProcess.Kill(entireProcessTree: true);
                clientProcess.WaitForExit(3000);
            }
        }
        catch { }
        clientProcess = null;

        Shared.Logger.LogInformation("[Client] Launching ARI.Client...");
        Environment.SetEnvironmentVariable("ARI_BASE_URL", $"http://localhost:{port}");

        var psi = OperatingSystem.IsMacOS()
            ? new System.Diagnostics.ProcessStartInfo("open", $"-a Terminal \"{scriptPath}\"") { UseShellExecute = false }
            : new System.Diagnostics.ProcessStartInfo("/bin/bash", $"\"{scriptPath}\"") { UseShellExecute = true, CreateNoWindow = false };

        try { clientProcess = System.Diagnostics.Process.Start(psi); }
        catch (Exception ex) { Shared.Logger.LogWarning("[Client] Failed to launch client: {Error}", ex.Message); }
    }

    private static string ResolvePath(string baseDir, string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDir, path));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (startupFailed) return;

        Shared.Logger.LogInformation("ARI is shutting down...");

        try
        {
            if (clientProcess is not null && !clientProcess.HasExited)
            {
                Shared.Logger.LogInformation("[Client] Stopping client process...");
                clientProcess.Kill(entireProcessTree: true);
            }
        }
        catch { }
        clientProcess = null;

        if (discordService != null)
            await discordService.NotifyOffline();

        speechQueue?.Dispose();
        synthesiser?.Dispose();

        llmModule?.StopAllServersAsync();
        llmModule?.Dispose();

        if (apiModule is not null)
            await apiModule.Stop(cancellationToken);

        if (docker != null && docker.containersRunning)
            await docker.StopContainers();

        await base.StopAsync(cancellationToken);
    }
}

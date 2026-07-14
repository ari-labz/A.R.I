using ARI.Common;
using ARI.Scheduler;
using CommonModules = ARI.Common.Modules;
using ARI.Core.Scripts;
using ARI.Discord;
using ARI.LLM;
using ARI.Voice;
using ARI.VoiceSynthesis;
using ARI.API;
using ARI.API.Data;
using ARI.Listener;
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
    public VoiceModule?           voiceModule;
    public VoiceSynthesisModule?  voiceSynthesisModule;
    public ListenerModule?        listenerModule;
    private LLMModule?           llmModule;
    private SchedulerModule?     schedulerModule;

    private readonly ILoggerFactory loggerFactory;
    private ILogger _logger = Shared.Logger;
    private StyleTtsSynthesiser? synthesiser;
    private SpeechQueue?         speechQueue;
    private bool startupFailed;
    private static System.Diagnostics.Process? clientProcess;

    public ARI(ILoggerFactory loggerFactory)
    {
        this.loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("ARI.Core");
        Shared.InitialiseLogger(loggerFactory, "ARI.Core");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try   { await Startup(stoppingToken); }
        catch (Exception ex)
        {
            startupFailed = true;
            _logger.LogCritical("Startup failed: {Error}", ex.Message);
            throw;
        }
    }

    private async Task Startup(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ARI is starting...");

        // Clear any stale ARI instance (and its child servers) before we bind ports — otherwise a
        // leftover process from a terminal launch blocks a fresh run from Rider.
        ProcessGuard.KillStaleInstances(_logger);

        config = AriConfig.Load();

        // Resolve paths up front so all modules see consistent, absolute paths. An explicit config
        // value always wins (ResolveOverride handles relative-vs-absolute); otherwise everything
        // defaults through Paths — the single source of truth for every on-disk location.
        config.modules.VoiceSynthesis.StyleTtsPath = !string.IsNullOrEmpty(config.modules.VoiceSynthesis.StyleTtsPath)
            ? Paths.ResolveOverride(config.modules.VoiceSynthesis.StyleTtsPath)
            : Paths.StyleTts2Source;

        // Voices are user data, not install content — default under AppData unless overridden.
        config.modules.VoiceSynthesis.VoicesPath = !string.IsNullOrEmpty(config.modules.VoiceSynthesis.VoicesPath)
            ? Paths.ResolveOverride(config.modules.VoiceSynthesis.VoicesPath)
            : Paths.Voices;

        // StyleTTS2's mutable working state (venv, per-model training work dirs, the downloaded
        // pretrained checkpoint cache) — always AppData, never inside StyleTtsPath (install content,
        // may be read-only / replaced wholesale on update).
        config.modules.VoiceSynthesis.DataDir = Paths.StyleTts2Data;

        await Dependency.CheckPython();
        await Dependency.CheckHomebrew();
        await Dependency.CheckLlamaCpp();

        // ── Shared infrastructure ────────────────────────────────────────────────
        PersistentData persistentData = new();

        string ariPersistentDir = Paths.PersistentData;
        // Agents.json is now source-controlled (ARI.Core/Agents.json, copied to the output dir at build) —
        // edited in the repo, not in PersistentData. Load the built copy directly.
        string agentsPath = Path.Combine(Paths.BuildPath, "Agents.json");

        // ── LLM module ───────────────────────────────────────────────────────────
        // Models are large and often already live elsewhere (another app's model library) — an
        // explicit config value wins, otherwise Paths.Models (AppData, or MODELS_PATH if set).
        string modelsPath = !string.IsNullOrEmpty(config.modules.LLM.ModelsPath)
            ? Paths.ResolveOverride(config.modules.LLM.ModelsPath)
            : Paths.Models;

        if (config.modules.LLM.Enabled)
        {
            BrainConfig? brainConfig = config.modules.Brain?.Enabled == true ? config.modules.Brain : null;

            _logger.LogInformation("Loading agents...");
            llmModule = new LLMModule(
                servers:        persistentData.GetServers().ToList(),
                agentsJsonPath: agentsPath,
                brainConfig:    brainConfig,
                loggerFactory:  loggerFactory);
            CommonModules.Register(llm: llmModule);
            _logger.LogInformation("Agents loaded.");
        }

        // ── Voice setup ──────────────────────────────────────────────────────────

        voiceSynthesisModule = new VoiceSynthesisModule();
        CommonModules.Register(voiceSynthesis: voiceSynthesisModule);

        if (config.modules.VoiceSynthesis.Enabled)
        {
            _logger.LogInformation("VoiceSynthesis module is enabled. Installing StyleTTS2...");
            string sttPath    = config.modules.VoiceSynthesis.StyleTtsPath;
            string sttDataDir = config.modules.VoiceSynthesis.DataDir;
            await new StyleTtsSetupService(sttPath, sttDataDir, loggerFactory.CreateLogger("ARI.VoiceSynthesis")).Install();
            voiceSynthesisModule.MarkSetupComplete();
            _logger.LogInformation("VoiceSynthesis ready.");
        }

        if (config.modules.Voice.Enabled)
        {
            string sttPath    = config.modules.VoiceSynthesis.StyleTtsPath;
            string sttDataDir = config.modules.VoiceSynthesis.DataDir;
            string voicesPath = config.modules.VoiceSynthesis.VoicesPath;
            string modelName  = persistentData.GetDefaultVoiceModel() ?? config.modules.Voice.ModelName;
            if (!Directory.Exists(Path.Combine(voicesPath, modelName)) && modelName != config.modules.Voice.ModelName)
            {
                _logger.LogWarning("Default voice '{Model}' no longer exists — falling back to {Fallback}.", modelName, config.modules.Voice.ModelName);
                modelName = config.modules.Voice.ModelName;
            }
            string modelDir   = Path.Combine(voicesPath, modelName);
            string modelPath  = Path.Combine(modelDir, "model.pth");
            string configPath = Path.Combine(modelDir, "config.yml");
            string refAudio   = FindReferenceAudio(modelDir, sttDataDir, modelName);

            if (!File.Exists(modelPath) || !File.Exists(configPath))
                _logger.LogWarning("Voice module enabled but no model found at {Path} — skipping.", modelDir);
            else if (string.IsNullOrEmpty(refAudio))
                _logger.LogWarning("Voice module enabled but no reference audio found for {Model} — skipping.", modelName);
            else
            {
                ILogger voiceLogger = loggerFactory.CreateLogger("ARI.Voice");
                _logger.LogInformation("Voice loading model: {Model}", modelName);
                synthesiser = new StyleTtsSynthesiser(sttPath, sttDataDir, modelPath, configPath, refAudio, voiceLogger);
                await synthesiser.Start(stoppingToken);
                try { await synthesiser.Warmup(stoppingToken); }
                catch (Exception ex) { _logger.LogError("Voice warmup failed (model may have corrupt weights): {Error}", ex.Message); }

                speechQueue = new SpeechQueue(synthesiser, voiceLogger);
                string pythonPath = Path.Combine(sttDataDir, OperatingSystem.IsWindows() ? @"venv\Scripts\python.exe" : "venv/bin/python");
                speechQueue.AudioReady += wav => PlayAudio(wav, pythonPath, voiceLogger);

                voiceModule = new VoiceModule(synthesiser, speechQueue, modelName);
                CommonModules.Register(voice: voiceModule);
                _logger.LogInformation("Voice ready.");
            }
        }

        // ── API ──────────────────────────────────────────────────────────────────
        if (config.modules.API.Enabled)
        {
            _logger.LogInformation("Web panel module is enabled. Starting on port {Port}...", config.modules.API.Port);

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
            _logger.LogInformation("Starting LLM servers...");
            await llmModule.StartServersAsync(persistentData.GetModels().ToList(), modelsPath);

            foreach (var agent in persistentData.GetAgents())
            {
                llmModule.AssignAgentServer(agent.Name, agent.ServerName);
                if (agent.Slot.HasValue) llmModule.AssignAgentSlot(agent.Name, agent.Slot.Value);
            }
        }

        // ── Listener (audio hub) ───────────────────────────────────────────────────
        if (config.modules.Listener.Enabled && llmModule is not null)
        {
            _logger.LogInformation("Listener module is enabled. Installing Whisper worker environment...");
            config.modules.Listener.ScriptPath = !string.IsNullOrEmpty(config.modules.Listener.ScriptPath)
                ? Paths.ResolveOverride(config.modules.Listener.ScriptPath)
                : Paths.ListenerScript;

            // "python3" is ListenerConfig's own default (i.e. "not customized") — provision and use
            // a dedicated venv unless the user explicitly pointed PythonPath somewhere themselves.
            if (config.modules.Listener.PythonPath == "python3")
            {
                config.modules.Listener.PythonPath = await new ListenerSetupService(loggerFactory.CreateLogger("ARI.Listener")).Install();
            }

            _logger.LogInformation("Starting audio hub...");
            listenerModule = new ListenerModule(llmModule, config.modules.Listener, loggerFactory.CreateLogger("ARI.Listener"));
            listenerModule.Start();
            CommonModules.Register(listener: listenerModule);
            _logger.LogInformation("Listener ready (whisper worker running: {Running}).", listenerModule.IsReady);
        }

        // ── Discord ──────────────────────────────────────────────────────────────
        List<Task> moduleTasks = new();

        if (config.modules.Discord.Enabled)
        {
            _logger.LogInformation("Discord module is enabled. Starting...");
            discordService = new DiscordModule(loggerFactory, llmModule, config.modules.Discord);
            await discordService.StartAsync(stoppingToken);
            if (discordService.ExecuteTask is not null)
                moduleTasks.Add(discordService.ExecuteTask);

            CommonModules.Register(discord: discordService);
        }

        // ── Scheduler ─────────────────────────────────────────────────────────────
        if (config.modules.Scheduler.Enabled && llmModule is not null)
        {
            schedulerModule = new SchedulerModule(config.modules.Scheduler, ariPersistentDir, loggerFactory.CreateLogger("ARI.Scheduler"));

            // Tidy walk: every 6 hours, while idle, Refactor restructures the graph (hubs, dedup, types).
            if (llmModule.HasRefactor)
                schedulerModule.AddTask("Refactor", "0 */6 * * *", ct => llmModule.RunRefactorAsync(ct));

            // Curiosity walk: every 6 hours, while idle, the Curiosity agent explores the graph and records
            // open questions to Curiosities.json (BrainScan's successor). Staggered off Refactor's slot so
            // the two brain walks don't fire together.
            if (llmModule.HasCuriosity)
                schedulerModule.AddTask("Curiosity", "0 3,9,15,21 * * *", ct => llmModule.RunCuriosityAsync(ct));

            // Proactive message: every 2 hours (while idle, outside quiet hours), Ari opens a thread + pushes.
            // The enable switch and quiet-hours window are read LIVE from the scheduler each fire, so control-
            // panel edits take effect without a restart.
            LLMModule llm = llmModule;
            SchedulerModule sched = schedulerModule;
            schedulerModule.AddTask("ProactiveMessage", "0 */2 * * *", async ct =>
            {
                if (!sched.ProactiveEnabled)
                {
                    _logger.LogInformation("[Scheduler] proactive message held — disabled.");
                    return;
                }
                if (sched.IsQuietHour(DateTime.Now.Hour))
                {
                    _logger.LogInformation("[Scheduler] proactive message held — quiet hours.");
                    return;
                }
                await llm.RunProactiveMessageAsync(ariPersistentDir, ct);
            }, uninterruptible: true);

            CommonModules.Register(scheduler: schedulerModule);
            schedulerModule.Start();
        }

        if (config.modules.API.Enabled)
            LaunchClient(Paths.BuildPath, config.modules.API.Port);

        _logger.LogInformation("ARI is ready.");

        if (moduleTasks.Count > 0)
            await Task.WhenAny(moduleTasks.Concat(new[] { Task.Delay(Timeout.Infinite, stoppingToken) }));
        else
            await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private static string FindReferenceAudio(string modelDir, string sttDataDir, string modelName)
    {
        string bundled = Path.Combine(modelDir, "reference.wav");
        if (File.Exists(bundled)) return bundled;

        string audioDir = Path.Combine(sttDataDir, "Data", modelName, "wavs");
        if (!Directory.Exists(audioDir)) return "";
        string[] wavs = Directory.GetFiles(audioDir, "*.wav");
        return wavs.Length > 0 ? wavs.OrderBy(f => f).First() : "";
    }

    // Only one audio-output stream at a time — otherwise back-to-back sentences (Speech pipeline) spawn
    // overlapping sd.play processes that fight for the device (PortAudio -9986).
    private static readonly SemaphoreSlim playLock = new(1, 1);

    private static void PlayAudio(byte[] wav, string python, ILogger logger)
    {
        _ = Task.Run(async () =>
        {
            await playLock.WaitAsync();
            string tmp        = Path.Combine(Path.GetTempPath(), $"ari_speech_{Guid.NewGuid():N}.wav");
            string scriptPath = Path.Combine(Path.GetTempPath(), $"ari_play_{Guid.NewGuid():N}.py");
            try
            {
                File.WriteAllBytes(tmp, wav);
                File.WriteAllText(scriptPath,
                    "import sounddevice as sd, soundfile as sf\n" +
                    $"data, sr = sf.read(r'{tmp}')\n" +
                    "sd.play(data, sr, blocking=True)\n" +
                    $"import os; os.remove(r'{tmp}')\n");

                System.Diagnostics.Process? proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName              = python,
                    Arguments             = $"\"{scriptPath}\"",
                    UseShellExecute       = false,
                    RedirectStandardError = true,
                });
                if (proc == null) { logger.LogError("[Voice] Failed to start audio playback"); return; }

                string stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (proc.ExitCode != 0) logger.LogError("[Voice] Playback failed: {Error}", stderr);
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
                playLock.Release();
            }
        });
    }

    private void LaunchClient(string executableDirectory, int port)
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
            _logger.LogWarning("[Client] setup.sh not found — skipping client launch.");
            return;
        }

        try
        {
            if (clientProcess is not null && !clientProcess.HasExited)
            {
                _logger.LogInformation("[Client] Stopping previous client instance (PID {Pid})...", clientProcess.Id);
                clientProcess.Kill(entireProcessTree: true);
                clientProcess.WaitForExit(3000);
            }
        }
        catch { }
        clientProcess = null;

        _logger.LogInformation("[Client] Launching ARI.Client...");
        Environment.SetEnvironmentVariable("ARI_BASE_URL", $"http://localhost:{port}");

        var psi = OperatingSystem.IsMacOS()
            ? new System.Diagnostics.ProcessStartInfo("open", $"-a Terminal \"{scriptPath}\"") { UseShellExecute = false }
            : new System.Diagnostics.ProcessStartInfo("/bin/bash", $"\"{scriptPath}\"") { UseShellExecute = true, CreateNoWindow = false };

        try { clientProcess = System.Diagnostics.Process.Start(psi); }
        catch (Exception ex) { _logger.LogWarning("[Client] Failed to launch client: {Error}", ex.Message); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (startupFailed) return;

        _logger.LogInformation("ARI is shutting down...");

        try
        {
            if (clientProcess is not null && !clientProcess.HasExited)
            {
                _logger.LogInformation("[Client] Stopping client process...");
                clientProcess.Kill(entireProcessTree: true);
            }
        }
        catch { }
        clientProcess = null;

        if (discordService != null)
            await discordService.NotifyOffline();

        speechQueue?.Dispose();
        synthesiser?.Dispose();

        schedulerModule?.Dispose();

        llmModule?.StopAllServersAsync();
        llmModule?.Dispose();

        if (apiModule is not null)
            await apiModule.Stop(cancellationToken);

        await base.StopAsync(cancellationToken);
    }
}

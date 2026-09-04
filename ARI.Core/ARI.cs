using ARI.Common;
using ARI.Scheduler;
using CommonModules = ARI.Common.Modules;
using ARI.Core.Scripts;
using ARI.Discord;
using Dependency    = ARI.Core.Scripts.Dependency;
using ARI.LLM;
using LLMDependency = ARI.LLM.Dependency;
using ARI.Voice;
using ARI.VoiceSynthesis;
using ARI.API;
using ARI.API.Data;
using ARI.Listener;
using ARI.BrainVault;
using ARI.ImageGen;
using ImageGenDependency = ARI.ImageGen.Dependency;
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
    private ImageGenModule?      imageGenModule;

    private readonly ILoggerFactory loggerFactory;
    private ILogger _logger = Shared.Logger;
    private ITtsSynthesiser? synthesiser;
    private SpeechQueue?    speechQueue;
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

        // Seed any missing app-data files from the bundled defaults before anything reads them.
        AppDataSeeder.Seed(Paths.AppDataDefaults, Paths.PersistentData);

        config = AriConfig.Load();

        Shared.ResolveDevMode(config.DevMode);
        if (Shared.DevMode)
            _logger.LogWarning("DevMode is ON — Engram is disabled; no memories will be written to the brain this run.");
        else
            _logger.LogInformation("DevMode is off — Engram is active.");

        // Before any module can make an LLM call, so no run goes unrecorded.
        SessionRecorder.Configure(config.Recording);

        // Resolve paths up front so all modules see consistent, absolute paths. An explicit config
        // value always wins (ResolveOverride handles relative-vs-absolute); otherwise everything
        // defaults through Paths — the single source of truth for every on-disk location.
        config.modules.VoiceSynthesis.StyleTtsPath = !string.IsNullOrEmpty(config.modules.VoiceSynthesis.StyleTtsPath)
            ? Paths.ResolveOverride(config.modules.VoiceSynthesis.StyleTtsPath)
            : Path.Combine(Paths.VoiceModules, "StyleTTS2");

        // Voices are user data, not install content — default under AppData unless overridden.
        config.modules.VoiceSynthesis.VoicesPath = !string.IsNullOrEmpty(config.modules.VoiceSynthesis.VoicesPath)
            ? Paths.ResolveOverride(config.modules.VoiceSynthesis.VoicesPath)
            : Paths.Voices;

        // StyleTTS2's mutable working state (venv, per-model training work dirs, the downloaded
        // pretrained checkpoint cache) — always AppData, never inside StyleTtsPath (install content,
        // may be read-only / replaced wholesale on update).
        config.modules.VoiceSynthesis.DataDir = Paths.StyleTts2Data;

        await Dependency.CheckPython();
        await Dependency.CheckEspeakNg();
        await Dependency.CheckLibDave();
        await Dependency.CheckLlamaCpp();
        Shared.LlamaCppUpdate = Dependency.UpdateLlamaCpp;
        Shared.LlamaCppSetPath = Dependency.SetLlamaCppPath;
        Shared.LlamaCppSuppressUpdates = Dependency.SuppressLlamaCppUpdates;
        Shared.LlamaCppEnableUpdates = Dependency.EnableLlamaCppUpdates;

        // ── Shared infrastructure ────────────────────────────────────────────────
        PersistentData persistentData = new();

        string ariPersistentDir = Paths.PersistentData;
        string agentsPath = Path.Combine(ariPersistentDir, "Agents.json");

        // ── LLM module ───────────────────────────────────────────────────────────
        // Models are large and often already live elsewhere (another app's model library) — an
        // explicit config value wins, otherwise Paths.Models (AppData, or MODELS_PATH if set).
        string modelsPath = !string.IsNullOrEmpty(config.modules.LLM.ModelsPath)
            ? Paths.ResolveOverride(config.modules.LLM.ModelsPath)
            : Paths.Models;

        if (config.modules.LLM.Enabled)
        {
            LLMDependency.StartSearXng();

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

        bool voiceSynthReady = false;
        if (config.modules.VoiceSynthesis.Enabled)
        {
            try
            {
                _logger.LogInformation("VoiceSynthesis module is enabled. Installing dependencies...");
                await new VoiceSynthesisSetupService(loggerFactory.CreateLogger("ARI.VoiceSynthesis")).Install();

                voiceSynthesisModule.MarkSetupComplete();
                _logger.LogInformation("VoiceSynthesis ready.");
                voiceSynthReady = true;
            }
            catch (Exception ex)
            {
                // Voice synthesis is optional — a setup failure (e.g. no torch wheel for this
                // platform) must not abort the whole server. Skip voice/speech and carry on.
                _logger.LogError("VoiceSynthesis setup failed — continuing without voice. {Error}", ex.Message);
                if (ex is SetupException { Hint: { } hint }) _logger.LogError("{Hint}", hint);
            }
        }

        if (config.modules.Voice.Enabled && voiceSynthReady)
        {
            string sttPath    = config.modules.VoiceSynthesis.StyleTtsPath;
            string sttDataDir = config.modules.VoiceSynthesis.DataDir;
            string voicesPath = config.modules.VoiceSynthesis.VoicesPath;
            string engine     = config.modules.Voice.DefaultEngine;

            MigrateVoicesDirectory(voicesPath, _logger);

            ILogger voiceLogger = loggerFactory.CreateLogger("ARI.Voice");

            // Resolve the model to boot: persisted default → config default → first found → none
            string? ResolveModel(string eng)
            {
                string engDir = Path.Combine(voicesPath, eng);
                if (!Directory.Exists(engDir)) return null;

                string? candidate = persistentData.GetDefaultVoiceModel(eng)
                    ?? (config.modules.Voice.DefaultModels.TryGetValue(eng, out var cfgModel)
                        && !string.IsNullOrWhiteSpace(cfgModel) ? cfgModel : null);

                if (candidate is not null && Directory.Exists(Path.Combine(engDir, candidate)))
                    return candidate;

                if (candidate is not null)
                    _logger.LogWarning("Default voice '{Model}' for {Engine} not found — picking first available.", candidate, eng);

                return Directory.GetDirectories(engDir).Select(Path.GetFileName).FirstOrDefault(n => n is not null);
            }

            string? modelName = ResolveModel(engine);
            if (modelName is null)
            {
                _logger.LogWarning("Voice module enabled but no voices found for engine '{Engine}' — skipping.", engine);
            }
            else
            {
                string modelDir = Path.Combine(voicesPath, engine, modelName);

                async Task<ITtsSynthesiser?> SynthFactory(string eng, string model)
                {
                    string dir = Path.Combine(voicesPath, eng, model);
                    return await CreateModuleSynthesiser(eng, dir, sttDataDir, voiceLogger);
                }

                ITtsSynthesiser? engineSynthesiser = await SynthFactory(engine, modelName);
                if (engineSynthesiser is null)
                    _logger.LogWarning("Voice module enabled but engine '{Engine}' could not be initialised for model '{Model}' — skipping.", engine, modelName);
                else
                {
                    _logger.LogInformation("Voice loading model: {Model} (engine: {Engine})", modelName, engine);
                    synthesiser = engineSynthesiser;
                    await synthesiser.Start(stoppingToken);
                    try { await synthesiser.Warmup(stoppingToken); }
                    catch (Exception ex) { _logger.LogError("Voice warmup failed (model may have corrupt weights): {Error}", ex.Message); }

                    speechQueue = new SpeechQueue(synthesiser, voiceLogger);
                    string modulePy = Path.Combine(Paths.VoiceModules, engine, "venv",
                        OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python3");
                    string pythonPath = File.Exists(modulePy) ? modulePy : "python3";
                    void WireAudio(SpeechQueue q) => q.AudioReady += wav => PlayAudio(wav, pythonPath, voiceLogger);
                    WireAudio(speechQueue);

                    voiceModule = new VoiceModule(synthesiser, speechQueue, modelName, SynthFactory, WireAudio, voiceLogger);
                    CommonModules.Register(voice: voiceModule);
                    _logger.LogInformation("Voice ready.");
                }
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
                if (agent.SlotName is { Length: > 0 }) llmModule.AssignAgentSlot(agent.Name, agent.SlotName);
            }
        }

        // ── Listener (audio hub) ───────────────────────────────────────────────────
        if (config.modules.Listener.Enabled && llmModule is not null)
        {
            try
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
            catch (Exception ex)
            {
                // Listener is optional — a setup failure (e.g. no C++ build tools for webrtcvad) must
                // not abort the server.
                _logger.LogError("Listener setup failed — continuing without voice input. {Error}", ex.Message);
                if (ex is SetupException { Hint: { } hint }) _logger.LogError("{Hint}", hint);
                listenerModule = null;
            }
        }

        // ── ImageGen ─────────────────────────────────────────────────────────────
        if (config.modules.ImageGen.Enabled)
        {
            try
            {
                _logger.LogInformation("ImageGen module is enabled. Checking ComfyUI...");
                await ImageGenDependency.Check(config.modules.ImageGen.ComfyUiPath);

                if (string.IsNullOrEmpty(ImageGenDependency.Status))
                {
                    imageGenModule = new(config.modules.ImageGen);
                    CommonModules.Register(imageGen: imageGenModule);
                    _logger.LogInformation("ImageGen ready.");
                }
                else
                {
                    _logger.LogWarning("ImageGen unavailable: {Reason}", ImageGenDependency.Status);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("ImageGen setup failed — continuing without image generation. {Error}", ex.Message);
            }
        }

        // ── Discord ──────────────────────────────────────────────────────────────
        List<Task> moduleTasks = new();

        // Discord.json (app data, control-panel edited) is the source of truth. On first run it is
        // seeded from the AriConfig.json section so existing setups keep working — including a token
        // still coming from secrets.env, which is copied in and can then be deleted from there.
        if (!DiscordStore.Exists())
        {
            DiscordConfig seed = config.modules.Discord;
            string seedToken = seed.Token.Contains("${") ? "" : seed.Token;
            DiscordStore.Set(new DiscordSettings
            {
                Enabled            = seed.Enabled,
                Token              = seedToken,
                OwnerId            = seed.OwnerId,
                WhitelistedUserIds = seed.WhitelistedUserIds ?? [],
                WatchedChannelIds  = seed.WatchedChannelIds,
                AllowedGuildIds    = seed.AllowedGuildIds,
            });
            _logger.LogInformation("Discord settings seeded to Discord.json from AriConfig.json.");
        }

        DiscordSettings discord = DiscordStore.Get();

        if (discord.Enabled)
        {
            if (string.IsNullOrWhiteSpace(discord.Token))
            {
                _logger.LogWarning("Discord module is enabled but no token is set — add one in the control panel. Skipping Discord.");
            }
            else
            {
                _logger.LogInformation("Discord module is enabled. Starting...");
                discordService = new DiscordModule(loggerFactory, llmModule, new DiscordConfig
                {
                    Enabled            = discord.Enabled,
                    Token              = discord.Token,
                    OwnerId            = discord.OwnerId,
                    WhitelistedUserIds = discord.WhitelistedUserIds,
                    WatchedChannelIds  = discord.WatchedChannelIds,
                    AllowedGuildIds    = discord.AllowedGuildIds,
                });
                await discordService.StartAsync(stoppingToken);
                if (discordService.ExecuteTask is not null)
                    moduleTasks.Add(discordService.ExecuteTask);

                CommonModules.Register(discord: discordService);
            }
        }

        // ── Scheduler ─────────────────────────────────────────────────────────────
        if (config.modules.Scheduler.Enabled && llmModule is not null)
        {
            schedulerModule = new SchedulerModule(config.modules.Scheduler, ariPersistentDir, loggerFactory.CreateLogger("ARI.Scheduler"));

            // Tidy walk: once a day (04:00 UTC), Refactor restructures the graph (hubs, dedup, types),
            // capped at 10 seeds/run and rotating through the vault least-recently-refactored first.
            // Activity-aware: if Ari is in conversation at fire time the slot is deferred (30 min ×3) and
            // then dropped until the next day.
            if (llmModule.HasRefactor)
                schedulerModule.AddTask("Refactor", "0 4 * * *", ct => llmModule.RunRefactorAsync(ct), respectActivity: true);

            // Curiosity and ProactiveMessage are retired — Dreaming (see DreamOrchestrator) is their
            // successor: instead of a scheduled graph-walk queuing questions for a separate scheduled task
            // to raise later, Ari explores during idle dream time and wakes with a message when something
            // clears the bar, via the same CreateProactiveDialogueThread + push-notify path.

            schedulerModule.TaskStateChanged += (name, running) => llmModule.BroadcastTaskState(name, running);
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

    private static readonly string[] KnownEngines = ["StyleTTS2", "IndexTTS"];

    private static void MigrateVoicesDirectory(string voicesPath, ILogger logger)
    {
        if (!Directory.Exists(voicesPath)) return;

        foreach (string dir in Directory.GetDirectories(voicesPath))
        {
            string name = Path.GetFileName(dir);
            if (KnownEngines.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

            if (File.Exists(Path.Combine(dir, "model.pth")) || File.Exists(Path.Combine(dir, "config.yml")))
            {
                string dest = Path.Combine(voicesPath, "StyleTTS2", name);
                if (Directory.Exists(dest)) continue;
                Directory.CreateDirectory(Path.Combine(voicesPath, "StyleTTS2"));
                Directory.Move(dir, dest);
                logger.LogInformation("[Voice] Migrated voice '{Name}' → Voices/StyleTTS2/{Name}", name, name);
            }
        }
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

    private static readonly Dictionary<string, int> ModulePorts = new()
    {
        ["StyleTTS2"] = 8021,
        ["IndexTTS"] = 8026,
    };

    private async Task<ITtsSynthesiser?> CreateModuleSynthesiser(string engine, string voiceDir, string dataDir, ILogger voiceLogger)
    {
        string modulePath = Path.Combine(Paths.VoiceModules, engine);
        try
        {
            await new VoiceModuleSetupService(engine, _logger).Install();
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to download/install voice module '{Engine}': {Error}", engine, ex.Message);
            return null;
        }

        if (!Directory.Exists(modulePath))
        {
            _logger.LogWarning("Voice module '{Engine}' not found at {Path} — skipping.", engine, modulePath);
            return null;
        }

        if (!Directory.Exists(voiceDir))
        {
            _logger.LogWarning("Voice directory {Path} does not exist — skipping.", voiceDir);
            return null;
        }

        int port = ModulePorts.GetValueOrDefault(engine, 8021 + ModulePorts.Count);

        List<string> extraArgs = new List<string>();
        if (!string.IsNullOrEmpty(dataDir))
            extraArgs.Add($"--data-dir \"{dataDir}\"");
        if (PhonemeSubstitutions.Path is { } subsPath)
            extraArgs.Add($"--phoneme-subs \"{subsPath}\"");
        extraArgs.Add("--cpu");

        return new VoiceModuleSynthesiser(modulePath, voiceDir, port, voiceLogger,
            extraArgs: string.Join(" ", extraArgs));
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

        System.Diagnostics.ProcessStartInfo psi = OperatingSystem.IsMacOS()
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

        imageGenModule?.Shutdown();
        LLMDependency.StopSearXng();

        if (apiModule is not null)
            await apiModule.Stop(cancellationToken);

        await base.StopAsync(cancellationToken);
    }
}

using ARI.API.Data;
using ARI.Common;
using ARI.ImageGen;
using ARI.LLM;
using ARI.Voice;
using ARI.VoiceSynthesis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ARI.API.Controllers;

// ── Voice Synthesis API ───────────────────────────────────────────────────────

[Route("api/v1/admin/voice")]
[ApiController]
public class VoiceController(
    VoiceSynthesisConfig vsConfig,
    ILoggerFactory loggerFactory,
    IHostApplicationLifetime lifetime,
    PersistentData persistentData) : ControllerBase
{
    private readonly ILogger logger = loggerFactory.CreateLogger("ARI.WebPanel");
    private VoiceSynthesisModule? voiceTraining => (VoiceSynthesisModule?)Modules.VoiceSynthesis;
    private VoiceModule?          voiceService  => (VoiceModule?)Modules.Voice;
    private LLMModule?            llm           => (LLMModule?)Modules.Llm;
    private const int DATASET_POLL_MS = 2000;
    private static readonly string StagingRoot = Path.Combine(Path.GetTempPath(), "ari-voice-staging");
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> chunkCounters = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> assembleLocks = new();

    /// <summary>Wipes all staging data. Called on startup so uploads/processing output
    /// never persist across restarts or reboots — the user keeps only the zip they download.</summary>
    public static void ClearStaging()
    {
        try { if (Directory.Exists(StagingRoot)) Directory.Delete(StagingRoot, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [HttpPost("uploads")]
    public IActionResult CreateStage()
    {
        string stageId  = Guid.NewGuid().ToString("N");
        string stageDir = Path.Combine(StagingRoot, stageId);
        Directory.CreateDirectory(stageDir);
        return Ok(new { stageId, stagingPath = stageDir });
    }

    [HttpPost("uploads/{id}")]
    [RequestSizeLimit(1024L * 1024 * 1024)]
    public async Task<IActionResult> Upload(
        [FromRoute(Name = "id")] string stageId,
        [FromQuery] string name,
        [FromQuery] int chunk       = 0,
        [FromQuery] int totalChunks = 1)
    {
        try
        {
        if (string.IsNullOrWhiteSpace(stageId) || string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "stageId and name are required." });

        string stageDir = Path.Combine(StagingRoot, stageId);
        if (!Directory.Exists(stageDir))
            return BadRequest(new { error = "Unknown stageId. Make one with POST /admin/voice/uploads first." });

        if (totalChunks < 1 || totalChunks > 100_000 || chunk < 0 || chunk >= totalChunks)
            return BadRequest(new { error = "chunk must be between 0 and totalChunks - 1." });

        string safeName  = Path.GetFileName(name);
        string chunkPath = Path.Combine(stageDir, $"{safeName}.part{chunk}");

        // Write chunk fully before incrementing the counter — guarantees file is closed when count is read
        await using (System.IO.FileStream fs = System.IO.File.Create(chunkPath))
            await Request.Body.CopyToAsync(fs);

        string counterKey    = $"{stageId}:{safeName}";
        // Counted from the pieces on disk, so a piece sent twice is still only one piece.
        int    completedCount = Directory.GetFiles(stageDir, $"{safeName}.part*").Length;
        logger.LogInformation("[Voice] Chunk {Chunk}/{Total} written for {Name} ({Done} done)", chunk + 1, totalChunks, safeName, completedCount);

        // Only assemble once all chunks are fully written — atomic counter guarantees no partial files
        if (completedCount == totalChunks)
        {
            string assembleKey = $"{stageId}:{safeName}:assemble";
            SemaphoreSlim gate = assembleLocks.GetOrAdd(assembleKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                string dest = Path.Combine(stageDir, safeName);
                if (!System.IO.File.Exists(dest))
                {
                    await using (System.IO.FileStream outFs = System.IO.File.Create(dest))
                    {
                        for (int i = 0; i < totalChunks; i++)
                        {
                            string part = Path.Combine(stageDir, $"{safeName}.part{i}");
                            await using System.IO.FileStream partFs = System.IO.File.OpenRead(part);
                            await partFs.CopyToAsync(outFs);
                        }
                    }
                    for (int i = 0; i < totalChunks; i++)
                        System.IO.File.Delete(Path.Combine(stageDir, $"{safeName}.part{i}"));
                    chunkCounters.TryRemove(counterKey, out _);
                    logger.LogInformation("[Voice] Assembled {Name} ({Chunks} chunks) → {Dir}", safeName, totalChunks, stageDir);
                }
            }
            finally
            {
                gate.Release();
                assembleLocks.TryRemove(assembleKey, out _);
            }
        }

        return Ok(new { stagingPath = stageDir, chunk, totalChunks });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Voice] Upload failed for chunk {Chunk} of {Name}", chunk, name);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>Start a training job from a previously uploaded staging path.</summary>
    [HttpPost("training")]
    public async Task<IActionResult> StartTraining([FromBody] TrainRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
            return BadRequest(new { error = "modelName is required." });
        if (!SafeNames.IsSafe(req.ModelName) || (req.Engine is not null && !SafeNames.IsSafe(req.Engine)))
            return BadRequest(new { error = "That name is not allowed." });
        if (string.IsNullOrWhiteSpace(req.StagingPath) || !Directory.Exists(req.StagingPath)
            || !Path.GetFullPath(req.StagingPath).StartsWith(Path.GetFullPath(StagingRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return BadRequest(new { error = "stagingPath does not exist." });
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis module is not configured." });

        TrainingJob job;
        try
        {
            if (voiceTraining?.IsSetupComplete != true)
                return StatusCode(503, new { error = "Voice module is still installing. Please wait." });

            string engine = req.Engine ?? "StyleTTS2";
            int port = engine switch { "IndexTTS" => 8026, "KokoroRVC" => 8027, _ => 8021 };
            string baseUrl = $"http://localhost:{port}";

            // Copy staging data into voice dir so it survives for resume
            string voiceDir = Path.Combine(vsConfig.VoicesPath, engine, req.ModelName);
            Directory.CreateDirectory(voiceDir);
            await EnsureEngineServer(engine, port, voiceDir);
            string dataDir = Path.Combine(voiceDir, "Data");
            Directory.CreateDirectory(dataDir);
            foreach (string file in Directory.GetFiles(req.StagingPath))
                System.IO.File.Copy(file, Path.Combine(dataDir, Path.GetFileName(file)), overwrite: true);

            IVoiceTrainer trainer = new VoiceModuleTrainer(
                baseUrl:      baseUrl,
                audioPath:    dataDir,
                voiceDir:     voiceDir,
                modelName:    req.ModelName,
                epochs:       req.Epochs,
                saveEvery:    req.SaveEveryNEpochs,
                retrain:      false,
                transcripts:  req.Transcripts,
                phonemeSubs:  PhonemeSubstitutions.Path,
                logger:       logger);

            job = voiceTraining!.Start(trainer, req.ModelName, lifetime.ApplicationStopping);

            TrainingSettings initialSettings = new TrainingSettings(dataDir, req.ModelName, req.Epochs, req.SaveEveryNEpochs, req.Transcripts);
            System.IO.File.WriteAllText(
                Path.Combine(voiceDir, "training.json"),
                JsonSerializer.Serialize(initialSettings));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        logger.LogInformation(
            "[Voice] Training started — engine: {Engine}, model: {ModelName}, epochs: {Epochs}",
            req.Engine, req.ModelName, req.Epochs);

        string stagingPath = req.StagingPath;
        string modelName   = req.ModelName;
        _ = Task.Run(async () =>
        {
            while (job.IsRunning)
                await Task.Delay(2000);

            if (job.IsSuccess)
            {
                try { Directory.Delete(stagingPath, recursive: true); }
                catch { /* best-effort */ }
                logger.LogInformation("[Voice] Voice Synthesis of {ModelName} complete", modelName);
                if (Modules.Llm is not null)
                    await Modules.Llm.NotifyOwner($"> Voice Synthesis of {modelName} complete");
            }
            else
            {
                logger.LogWarning("[Voice] Training failed for {ModelName}: {Error}", modelName, job.Error);
            }
        });

        return Ok(new { jobId = job.JobId, modelName = job.ModelName });
    }

    /// <summary>SSE stream of training progress events.</summary>
    [HttpGet("training/progress")]
    public async Task StreamProgress(CancellationToken ct)
    {
        Response.Headers[HeaderNames.ContentType]  = "text/event-stream";
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        Response.Headers["X-Accel-Buffering"]      = "no";

        TrainingJob? job = voiceTraining?.Current;
        if (job is null)
        {
            await Response.WriteAsync("data: {\"step\":\"Idle\",\"percent\":0}\n\n", ct);
            await Response.Body.FlushAsync(ct);
            return;
        }

        int  sent          = 0;
        long lastKeepalive = Environment.TickCount64;

        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<TrainingProgressEvent> events = job.Events;
            bool wrote = false;
            while (sent < events.Count)
            {
                TrainingProgressEvent ev   = events[sent++];
                string j = System.Text.Json.JsonSerializer.Serialize(new
                {
                    step    = ev.Step,
                    percent = ev.Percent,
                    detail  = ev.Detail,
                    ts      = ev.Timestamp,
                    done    = !job.IsRunning,
                    success = job.IsSuccess,
                    error   = job.Error,
                });
                await Response.WriteAsync($"data: {j}\n\n", ct);
                wrote = true;
            }

            // Send an SSE comment every 30 s so a reverse proxy/tunnel in front of ARI doesn't close the idle connection
            if (Environment.TickCount64 - lastKeepalive > 30_000)
            {
                await Response.WriteAsync(": keepalive\n\n", ct);
                lastKeepalive = Environment.TickCount64;
                wrote = true;
            }

            if (wrote) await Response.Body.FlushAsync(ct);

            if (!job.IsRunning) break;
            await Task.Delay(300, ct);
        }
    }

    /// <summary>Current job status (for polling fallback).</summary>
    [HttpGet("training")]
    public IActionResult GetStatus()
    {
        TrainingJob? job = voiceTraining?.Current;
        if (job is null)
            return Ok(new { idle = true });

        return Ok(new
        {
            jobId     = job.JobId,
            modelName = job.ModelName,
            isRunning = job.IsRunning,
            isSuccess = job.IsSuccess,
            error     = job.Error,
            events    = job.Events,
        });
    }

    [HttpPost("~/api/v1/speech/speak")]
    public async Task<IActionResult> Speak([FromBody] SpeakRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "text is required." });
        if (req.Text.Length > 5000)
            return BadRequest(new { error = "text is too long (5000 characters at most)." });
        if (await EnsureVoiceModule() is { } notReady) return notReady;
        if (voiceService?.IsReady != true)
            return StatusCode(503, new { error = "Voice module is not running." });

        Dictionary<string, object> engineParams = new Dictionary<string, object>();
        // Open to everyone signed in, so every knob is held to a sensible range (a huge step count would tie up the GPU).
        engineParams["diffusionSteps"] = Math.Clamp(req.DiffusionSteps, 1, 20);
        engineParams["alpha"]          = Math.Clamp(req.Alpha, 0f, 1f);
        engineParams["beta"]           = Math.Clamp(req.Beta, 0f, 1f);
        engineParams["embeddingScale"] = Math.Clamp(req.EmbeddingScale, 0.1f, 3f);
        engineParams["speed"]          = Math.Clamp(req.Speed, 0.5f, 2f);
        engineParams["pauseScale"]     = Math.Clamp(req.PauseScale, 0f, 3f);
        // A checkpoint is a file on the server's disk, so only an admin may name one; everyone else gets the active voice.
        if (!string.IsNullOrWhiteSpace(req.CheckpointPath) && User.IsInRole(ARI.API.Auth.Roles.Admin))
        {
            // The file is loaded by the speech engine, so it has to be one of the voices' own checkpoints.
            if (string.IsNullOrEmpty(vsConfig.VoicesPath)
                || !Path.GetFullPath(req.CheckpointPath).StartsWith(Path.GetFullPath(vsConfig.VoicesPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return BadRequest(new { error = "That checkpoint is not one of the installed voices." });
            if (!System.IO.File.Exists(req.CheckpointPath))
                return NotFound(new { error = $"Checkpoint not found: {req.CheckpointPath}" });
            // The engine keeps whichever checkpoint it loaded last, so "latest" (model.pth) is loaded the same way.
            if (voiceService.Synthesiser is VoiceModuleSynthesiser engineServer)
                await engineServer.LoadCheckpoint(req.CheckpointPath, ct);
        }

        byte[] wav = await voiceService.Synthesise(req.Text, engineParams.Count > 0 ? engineParams : null, ct);
        logger.LogInformation("[Voice/Speak] {Chars} characters → {Bytes} bytes", req.Text.Length, wav.Length);
        return File(wav, "audio/wav");
    }

    [HttpGet("models/{engine}/{id}/checkpoints")]
    public IActionResult GetCheckpoints(string engine, [FromRoute(Name = "id")] string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || modelName.Contains('/') || modelName.Contains('\\')
            || string.IsNullOrWhiteSpace(engine) || engine.Contains('/') || engine.Contains('\\'))
            return BadRequest(new { error = "Invalid model name." });
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis not configured." });

        string modelDir = Path.Combine(vsConfig.VoicesPath, engine, modelName);
        if (!Directory.Exists(modelDir))
            return NotFound(new { error = $"Model '{modelName}' not found." });

        List<object> checkpoints = new List<object>();

        string modelPth = Path.Combine(modelDir, "model.pth");
        if (System.IO.File.Exists(modelPth))
            checkpoints.Add(new { label = "latest", epoch = (int?)null, path = modelPth });

        // KokoroRVC keeps a playable copy of every save next to model.pth, named "<voice>_epoch<N>.pth".
        string epochPrefix = modelName + "_epoch";
        checkpoints.AddRange(Directory.GetFiles(modelDir, epochPrefix + "*.pth")
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f)[epochPrefix.Length..], out int epoch) ? (Epoch: epoch, Path: f) : (Epoch: -1, Path: f))
            .Where(e => e.Epoch >= 0)
            .OrderByDescending(e => e.Epoch)
            .Select(e => (object)new { label = $"epoch {e.Epoch}", epoch = (int?)e.Epoch, path = e.Path }));

        string checkpointsDir = Path.Combine(modelDir, "Checkpoints");
        if (Directory.Exists(checkpointsDir))
        {
            var epochEntries = Directory.GetDirectories(checkpointsDir)
                .Select(d => {
                    string folderName = Path.GetFileName(d);
                    string numPart = folderName.Replace("_epochs", "").Trim();
                    if (!int.TryParse(numPart, out int epoch)) return null;
                    string? pth = Directory.GetFiles(d, "*.pth").FirstOrDefault();
                    return pth is null ? null : new { label = $"epoch {epoch}", epoch = (int?)epoch, path = pth };
                })
                .Where(e => e is not null)
                .OrderByDescending(e => e!.epoch)
                .ToList();
            checkpoints.AddRange(epochEntries!);

            var looseEntries = Directory.GetFiles(checkpointsDir, "epoch_2nd_*.pth")
                .Select(f => {
                    string num = Path.GetFileNameWithoutExtension(f).Split('_').Last();
                    if (!int.TryParse(num, out int epoch)) return null;
                    return new { label = $"epoch {epoch}", epoch = (int?)epoch, path = f };
                })
                .Where(e => e is not null)
                .OrderByDescending(e => e!.epoch)
                .ToList();
            checkpoints.AddRange(looseEntries!);
        }

        return Ok(new { checkpoints });
    }

    [HttpPost("~/api/v1/speech/splitSentences")]
    public IActionResult SplitSentences([FromBody] SplitSentencesRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "text is required." });
        IReadOnlyList<string> sentences = ARI.Voice.SentenceSplitter.Split(req.Text);
        return Ok(new { sentences });
    }

    /// <summary>The voice ARI is speaking with right now, and how: model, engine, whether it is ready, the voice to
    /// start with next time, and the speed and pause settings (null if the voice module isn't running).</summary>
    [HttpGet("playback")]
    public IActionResult GetPlayback()
    {
        (float speed, float pauseScale) = voiceService?.GetVoiceSettings() ?? (0f, 0f);
        return Ok(new
        {
            model        = voiceService?.ActiveModel,
            engine       = voiceService?.ActiveEngine,
            ready        = voiceService?.IsReady ?? false,
            defaultModel = voiceService is not null ? persistentData.GetDefaultVoiceModel(voiceService.ActiveEngine) : null,
            speed        = voiceService is not null ? (float?)speed : null,
            pauseScale   = voiceService is not null ? (float?)pauseScale : null,
        });
    }

    public sealed record PlaybackPatch(float? Speed, float? PauseScale, string? DefaultModel, string? Engine);

    /// <summary>Change how ARI speaks: speed and pauses, and/or which voice to start with next time (<c>defaultModel</c>,
    /// for <c>engine</c>, or StyleTTS2 if none is given). Send only what changes.</summary>
    [HttpPatch("playback")]
    public IActionResult PatchPlayback([FromBody] PlaybackPatch req)
    {
        if (req.Engine is not null && !SafeNames.IsSafe(req.Engine)) return BadRequest(new { error = "That name is not allowed." });
        if (req.DefaultModel is { } model)
        {
            if (string.IsNullOrWhiteSpace(model) || model.Contains('/') || model.Contains('\\'))
                return BadRequest(new { error = "Invalid model name." });
            if (string.IsNullOrEmpty(vsConfig.VoicesPath))
                return StatusCode(503, new { error = "VoiceSynthesis not configured." });
            string engine = req.Engine ?? "StyleTTS2";
            if (!Directory.Exists(Path.Combine(vsConfig.VoicesPath, engine, model)))
                return NotFound(new { error = $"Model '{model}' not found." });
            persistentData.SetDefaultVoiceModel(engine, model);
            logger.LogInformation("[Voice] Default startup voice set to {Model} ({Engine})", model, engine);
        }

        if (req.Speed is not null || req.PauseScale is not null)
        {
            if (voiceService is null)
                return StatusCode(503, new { error = "Voice module is not running." });
            (float speed, float pauseScale) = voiceService.GetVoiceSettings();
            float newSpeed = req.Speed ?? speed, newPause = req.PauseScale ?? pauseScale;
            voiceService.SetVoiceSettings(newSpeed, newPause);
            logger.LogInformation("[Voice/Settings] speed={Speed} pause={Pause}", newSpeed, newPause);
        }

        return GetPlayback();
    }

    [HttpGet("playback/engineParams")]
    public IActionResult GetEngineParams()
    {
        if (voiceService is null)
            return StatusCode(503, new { error = "Voice module is not running." });
        var parameters = voiceService.GetEngineParameters().Select(p => new
        {
            p.Id, p.Label, p.Min, p.Max, p.Default, p.Step
        });
        return Ok(new { engine = voiceService.ActiveEngine, parameters });
    }

    [HttpPost("playback/switch")]
    public async Task<IActionResult> SwitchEngineModel([FromBody] SwitchEngineRequest req)
    {
        if (!SafeNames.IsSafe(req.Engine) || !SafeNames.IsSafe(req.ModelName))
            return BadRequest(new { error = "That name is not allowed." });
        if (await EnsureVoiceModule() is { } notReady) return notReady;
        // Loading a voice restarts its engine's server, which is the one a training run on that engine is using.
        if (voiceTraining?.Current is { IsRunning: true } && trainingEngine == req.Engine)
            return Conflict(new { error = $"A {req.Engine} voice is training on the same speech engine. Pause the training before switching voice." });
        if (string.IsNullOrWhiteSpace(req.Engine) || string.IsNullOrWhiteSpace(req.ModelName))
            return BadRequest(new { error = "engine and modelName are required." });
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis not configured." });

        string modelDir = Path.Combine(vsConfig.VoicesPath, req.Engine, req.ModelName);
        if (!Directory.Exists(modelDir))
            return NotFound(new { error = $"Model '{req.ModelName}' not found for engine {req.Engine}." });

        try
        {
            await voiceService.SwitchEngine(req.Engine, req.ModelName);
            persistentData.SetDefaultVoiceModel(req.Engine, req.ModelName);
            return Ok(new { ok = true, engine = req.Engine, model = req.ModelName });
        }
        catch (Exception ex)
        {
            logger.LogError("[Voice] Engine switch failed: {Error}", ex.Message);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("models")]
    public IActionResult GetModels()
    {
        if (string.IsNullOrEmpty(vsConfig.VoicesPath) || !Directory.Exists(vsConfig.VoicesPath))
            return Ok(new { models = Array.Empty<object>() });

        List<object> models = new List<object>();
        foreach (string engineDir in Directory.GetDirectories(vsConfig.VoicesPath))
        {
            string engine = Path.GetFileName(engineDir);
            foreach (string dir in Directory.GetDirectories(engineDir))
            {
                string? name = Path.GetFileName(dir);
                if (name is null) continue;
                string settingsFile = Path.Combine(dir, "training.json");
                TrainingSettings? settings = null;
                if (System.IO.File.Exists(settingsFile))
                {
                    try { settings = JsonSerializer.Deserialize<TrainingSettings>(System.IO.File.ReadAllText(settingsFile)); }
                    catch { }
                }
                models.Add(new
                {
                    name,
                    engine,
                    hasModel     = System.IO.File.Exists(Path.Combine(dir, "model.pth")),
                    hasResume    = settings is not null,
                    audioPath    = settings?.AudioPath,
                    epochs       = settings?.Epochs,
                    saveEveryN   = settings?.SaveEveryNEpochs,
                    latestEpoch  = LatestSavedEpoch(dir),
                    avatarVersion = FindAvatar(dir) is { } avatar ? new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(avatar)).ToUnixTimeSeconds() : (long?)null,
                });
            }
        }

        return Ok(new { models = models.OrderBy(m => ((dynamic)m).engine).ThenBy(m => ((dynamic)m).name).ToArray() });
    }

    [HttpDelete("models/{engine}/{id}")]
    public IActionResult DeleteModel(string engine, [FromRoute(Name = "id")] string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || modelName.Contains('/') || modelName.Contains('\\'))
            return BadRequest(new { error = "Invalid model name." });
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis not configured." });

        string dir = Path.Combine(vsConfig.VoicesPath, engine, modelName);
        if (!Directory.Exists(dir))
            return NotFound(new { error = $"Model '{modelName}' not found." });

        if (voiceTraining?.Current is { IsRunning: true } job && job.ModelName == modelName)
            return Conflict(new { error = "Pause or stop this model's training before deleting it." });

        Directory.Delete(dir, recursive: true);

        // RVC keeps its prepared clips and checkpoints under rvc/logs/<name> and its exported weights under
        // rvc/assets/weights; left behind, a new voice with the same name would silently reuse the old clips.
        if (engine == "KokoroRVC")
        {
            string rvc = Path.Combine(Paths.VoiceModules, engine, "rvc");
            string exp = RvcExperimentName(modelName);
            string expDir = Path.Combine(rvc, "logs", exp);
            if (Directory.Exists(expDir)) Directory.Delete(expDir, recursive: true);
            foreach (string weights in new[] { $"{exp}.pth", $"{exp}_export.pth" })
            {
                string path = Path.Combine(rvc, "assets", "weights", weights);
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
        }
        logger.LogInformation("[Voice] Deleted {Engine} model '{ModelName}'", engine, modelName);
        return Ok(new { ok = true, deleted = modelName, engine });
    }

    [HttpPost("models/{engine}/{id}/rename")]
    public async Task<IActionResult> RenameModel(string engine, [FromRoute(Name = "id")] string modelName, [FromBody] RenameModelRequest req)
    {
        string newName = (req.NewName ?? "").Trim();
        if (!SafeNames.IsSafe(newName))
            return BadRequest(new { error = "That name is not allowed." });
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis not configured." });
        if (newName == modelName)
            return Ok(new { ok = true, engine, name = newName });
        if (voiceTraining?.Current is { IsRunning: true } job && job.ModelName == modelName)
            return Conflict(new { error = "Pause or stop this model's training before renaming it." });

        string dir    = Path.Combine(vsConfig.VoicesPath, engine, modelName);
        string newDir = Path.Combine(vsConfig.VoicesPath, engine, newName);
        if (!Directory.Exists(dir))
            return NotFound(new { error = $"Model '{modelName}' not found." });
        bool caseOnly = string.Equals(modelName, newName, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(newDir) && !caseOnly)
            return Conflict(new { error = $"A {engine} model called '{newName}' already exists." });

        MoveDirectory(dir, newDir);

        // RVC keeps its training checkpoint under rvc/logs/<name>, keyed by the model name; move it too so resume still finds it.
        if (engine == "KokoroRVC")
        {
            string logs   = Path.Combine(Paths.VoiceModules, engine, "rvc", "logs");
            string oldExp = Path.Combine(logs, RvcExperimentName(modelName));
            string newExp = Path.Combine(logs, RvcExperimentName(newName));
            if (Directory.Exists(oldExp) && oldExp != newExp)
            {
                if (Directory.Exists(newExp) && !string.Equals(oldExp, newExp, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(newExp, recursive: true);
                MoveDirectory(oldExp, newExp);
            }
        }

        string settingsFile = Path.Combine(newDir, "training.json");
        if (System.IO.File.Exists(settingsFile))
        {
            try
            {
                JsonNode? node = JsonNode.Parse(System.IO.File.ReadAllText(settingsFile));
                if (node is JsonObject obj)
                {
                    obj["ModelName"] = newName;
                    System.IO.File.WriteAllText(settingsFile, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (Exception ex) { logger.LogWarning("[Voice] Could not update {File}: {Error}", settingsFile, ex.Message); }
        }

        if (persistentData.GetDefaultVoiceModel(engine) == modelName)
            persistentData.SetDefaultVoiceModel(engine, newName);
        if (voiceService is { } voice && voice.ActiveEngine == engine && voice.ActiveModel == modelName)
        {
            try { await voice.SwitchEngine(engine, newName); }
            catch (Exception ex) { logger.LogWarning("[Voice] Renamed model but could not reload it: {Error}", ex.Message); }
        }

        logger.LogInformation("[Voice] Renamed {Engine} model '{Old}' to '{New}'", engine, modelName, newName);
        return Ok(new { ok = true, engine, name = newName });
    }

    // Same rule as KokoroRVC's train.py uses for its experiment folder.
    private static string RvcExperimentName(string name) => Regex.Replace(name, "[^A-Za-z0-9_-]+", "_");

    // Directory.Move refuses a rename that only changes case on a case-insensitive disk, so go through a temporary name.
    private static void MoveDirectory(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            string temp = to + ".renaming";
            Directory.Move(from, temp);
            Directory.Move(temp, to);
        }
        else Directory.Move(from, to);
    }

    private static readonly Dictionary<string, string> AvatarTypes = new()
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".webp"] = "image/webp", [".gif"] = "image/gif",
    };
    private const long MAX_AVATAR_BYTES = 8 * 1024 * 1024;

    private static string? FindAvatar(string dir)
        => AvatarTypes.Keys.Select(ext => Path.Combine(dir, "avatar" + ext)).FirstOrDefault(System.IO.File.Exists);

    [HttpGet("models/{engine}/{id}/avatar")]
    public IActionResult GetAvatar(string engine, [FromRoute(Name = "id")] string modelName)
    {
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return NotFound();
        string? path = FindAvatar(Path.Combine(vsConfig.VoicesPath, engine, modelName));
        return path is null ? NotFound() : PhysicalFile(path, AvatarTypes[Path.GetExtension(path)]);
    }

    [HttpPost("models/{engine}/{id}/avatar")]
    [RequestSizeLimit(MAX_AVATAR_BYTES + 64 * 1024)]
    public async Task<IActionResult> SetAvatar(string engine, [FromRoute(Name = "id")] string modelName, IFormFile file)
    {
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis not configured." });
        string dir = Path.Combine(vsConfig.VoicesPath, engine, modelName);
        if (!Directory.Exists(dir))
            return NotFound(new { error = $"Model '{modelName}' not found." });
        string ext = Path.GetExtension(file?.FileName ?? "").ToLowerInvariant();
        if (file is null || file.Length == 0 || !AvatarTypes.ContainsKey(ext))
            return BadRequest(new { error = "Pick a PNG, JPG, WebP or GIF image." });
        if (file.Length > MAX_AVATAR_BYTES)
            return BadRequest(new { error = "That image is too big (8 MB max)." });

        while (FindAvatar(dir) is { } old) System.IO.File.Delete(old);
        await using (FileStream fs = System.IO.File.Create(Path.Combine(dir, "avatar" + ext)))
            await file.CopyToAsync(fs);
        return Ok(new { ok = true });
    }

    [HttpDelete("models/{engine}/{id}/avatar")]
    public IActionResult DeleteAvatar(string engine, [FromRoute(Name = "id")] string modelName)
    {
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis not configured." });
        string dir = Path.Combine(vsConfig.VoicesPath, engine, modelName);
        while (FindAvatar(dir) is { } old) System.IO.File.Delete(old);
        return Ok(new { ok = true });
    }

    [HttpDelete("models/{engine}/{id}/resume")]
    public IActionResult DismissResume(string engine, [FromRoute(Name = "id")] string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || modelName.Contains('/') || modelName.Contains('\\'))
            return BadRequest(new { error = "Invalid model name." });
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis not configured." });

        string dir = Path.Combine(vsConfig.VoicesPath, engine, modelName);
        if (!Directory.Exists(dir))
            return NotFound(new { error = $"Model '{modelName}' not found." });

        string settingsFile = Path.Combine(dir, "training.json");
        if (System.IO.File.Exists(settingsFile))
            System.IO.File.Delete(settingsFile);

        logger.LogInformation("[Voice] Dismissed resume for {Engine} model '{ModelName}'", engine, modelName);
        return Ok(new { ok = true, dismissed = modelName, engine });
    }

    // Training talks to the engine's own server, which only runs while that engine is the active voice. For any other
    // engine, start its server here (no idle stop, so it outlives a long run). StyleTTS2 is left out: it needs its own
    // startup arguments and is started by the voice module.
    private static readonly Dictionary<string, VoiceModuleSynthesiser> trainingServers = new();

    // The engine the current training run uses: a voice switch on that engine would restart the server it trains on.
    private static string? trainingEngine;

    /// <summary>Starts the Voice module if it isn't running (it skips loading at boot when no voice existed yet), so picking
    /// or testing a voice loads the module first and then the voice. Returns the error to send, or null when it is running.
    /// Refused while a voice trains: the module's speech engine would take over the port the training server uses.</summary>
    private async Task<IActionResult?> EnsureVoiceModule()
    {
        if (voiceService is not null) return null;
        if (voiceTraining?.Current is { IsRunning: true })
            return Conflict(new { error = "The voice module can't load while a voice is training (they share the speech engine). Pause the training first." });
        if (Modules.Lifecycle is not { } lifecycle)
            return StatusCode(503, new { error = "Voice module is not running." });
        logger.LogInformation("[Voice] Starting the voice module to load a voice.");
        string? error = await lifecycle.StartModule("Voice");
        return error is null && voiceService is not null ? null : StatusCode(503, new { error = error ?? "The voice module could not start." });
    }

    private async Task EnsureEngineServer(string engine, int port, string voiceDir)
    {
        trainingEngine = engine;
        // Every engine, StyleTTS2 included: the engine being trained is often not the one that's speaking.
        using (HttpClient probe = new() { Timeout = TimeSpan.FromSeconds(2) })
            try { if ((await probe.GetAsync($"http://localhost:{port}/health")).IsSuccessStatusCode) return; } catch { /* not running */ }

        logger.LogInformation("[Voice] Starting the {Engine} server for training.", engine);
        await new VoiceModuleSetupService(engine, logger).Install();
        VoiceModuleSynthesiser server = new(Path.Combine(Paths.VoiceModules, engine), voiceDir, port, logger);
        await server.Start();
        lock (trainingServers) trainingServers[engine] = server;
    }

    [HttpPost("training/stop")]
    public async Task<IActionResult> StopTraining([FromQuery] string modelName, [FromQuery] string? engine = "StyleTTS2")
    {
        engine ??= "StyleTTS2";
        int port = engine switch { "IndexTTS" => 8026, "KokoroRVC" => 8027, _ => 8021 };
        try
        {
            using HttpClient http = new();
            HttpResponseMessage resp = await http.PostAsync($"http://localhost:{port}/train/pause", null);
            resp.EnsureSuccessStatusCode();
            logger.LogInformation("[Voice] Pause requested for model '{ModelName}' on {Engine}", modelName, engine);
            return Ok(new { ok = true, stopping = modelName });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Voice] Failed to pause training for '{ModelName}'", modelName);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // Returns the highest epoch number found in the model's Checkpoints folder,
    // or from loose epoch_2nd_*.pth files if Checkpoints doesn't exist yet.
    private static int? LatestSavedEpoch(string modelDir)
    {
        // Check organised Checkpoints/<N>_epochs/ folder names first
        string checkpointsDir = Path.Combine(modelDir, "Checkpoints");
        if (Directory.Exists(checkpointsDir))
        {
            int? fromFolders = Directory.GetDirectories(checkpointsDir)
                .Select(d => {
                    string folderName = Path.GetFileName(d);
                    string numPart = folderName.Replace("_epochs", "").Trim();
                    return int.TryParse(numPart, out int n) ? (int?)n : null;
                })
                .Where(n => n is not null)
                .OrderByDescending(n => n)
                .FirstOrDefault();
            if (fromFolders is not null) return fromFolders;

            // Current layout: loose epoch_2nd_NNNNN.pth files inside Checkpoints/ (mid-training)
            int? fromCheckpointsDir = LatestEpochFromLooseFiles(checkpointsDir);
            if (fromCheckpointsDir is not null) return fromCheckpointsDir;
        }

        // Legacy layout: loose epoch_2nd_NNNNN.pth files directly in the model dir
        return LatestEpochFromLooseFiles(modelDir) ?? LatestEpochFromTrainLog(modelDir);
    }

    // KokoroRVC keeps no per-epoch files; its Train.log says where the playable voice came from: the epoch it last
    // paused at, or the last epoch of a finished run.
    private static int? LatestEpochFromTrainLog(string dir)
    {
        string log = Path.Combine(dir, "Train.log");
        if (!System.IO.File.Exists(log)) return null;
        int? saved = null, lastEpoch = null;
        try
        {
            foreach (string line in System.IO.File.ReadLines(log))
            {
                if (Regex.Match(line, @"^Epoch \[(\d+)/\d+\]") is { Success: true } e) lastEpoch = int.Parse(e.Groups[1].Value);
                else if (Regex.Match(line, @"Paused at epoch (\d+)") is { Success: true } p) saved = int.Parse(p.Groups[1].Value);
                else if (line.StartsWith("Done:", StringComparison.Ordinal)) saved = lastEpoch;
            }
        }
        catch (IOException) { /* being written to; try again next refresh */ }
        return saved;
    }

    private static int? LatestEpochFromLooseFiles(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "epoch_2nd_*.pth")
                .Select(f => {
                    string num = Path.GetFileNameWithoutExtension(f).Split('_').Last();
                    return int.TryParse(num, out int n) ? (int?)(n + 1) : null;
                })
                .Where(n => n is not null)
                .OrderByDescending(n => n)
                .FirstOrDefault()
            : null;

    [HttpGet("models/{engine}/{id}/lossHistory")]
    public IActionResult GetLossHistory(string engine, [FromRoute(Name = "id")] string modelName)
    {
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return Ok(new { points = Array.Empty<object>(), pauses = Array.Empty<int>() });

        string logPath = Path.Combine(vsConfig.VoicesPath, engine, modelName, "Train.log");
        if (!System.IO.File.Exists(logPath))
            return Ok(new { points = Array.Empty<object>(), pauses = Array.Empty<int>() });

        List<object> points    = new List<object>();
        List<int> pauses    = new List<int>();
        int lastEpoch = 0;
        // Epoch [N/total] — tracks current epoch from log lines
        Regex epochRe   = new Regex(@"Epoch \[(\d+)/", RegexOptions.Compiled);
        // Old-format: "Validation loss: X, Dur loss: Y, F0 loss: Z"
        Regex oldValRe  = new Regex(
            @"Validation loss:\s*([\d.]+),\s*Dur loss:\s*([\d.]+),\s*F0 loss:\s*([\d.]+)",
            RegexOptions.Compiled);

        try
        {
            foreach (string rawLine in System.IO.File.ReadLines(logPath))
            {
                // Strip INFO timestamp prefix if present
                string line = rawLine;
                if (line.StartsWith("INFO:", StringComparison.Ordinal))
                {
                    int colon = line.IndexOf(": ", 5, StringComparison.Ordinal);
                    if (colon >= 0) line = line[(colon + 2)..];
                }

                // Track epoch from step lines
                Match em = epochRe.Match(line);
                if (em.Success && int.TryParse(em.Groups[1].Value, out int ep))
                    lastEpoch = ep;

                if (rawLine.StartsWith("LOSS_JSON: ", StringComparison.Ordinal))
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(rawLine["LOSS_JSON: ".Length..]);
                        JsonElement root = doc.RootElement;
                        int jsonEp = root.GetProperty("epoch").GetInt32();
                        lastEpoch  = jsonEp;
                        double? sty  = root.TryGetProperty("sty",  out JsonElement styEl)  ? styEl.GetDouble()  : (double?)null;
                        double? diff = root.TryGetProperty("diff", out JsonElement diffEl) ? diffEl.GetDouble() : (double?)null;
                        double? dur  = root.TryGetProperty("dur",  out JsonElement durEl)  ? durEl.GetDouble()  : (double?)null;
                        points.Add(new { epoch = jsonEp, val = root.GetProperty("val").GetDouble(), f0 = root.GetProperty("f0").GetDouble(), sty, diff, dur });
                    }
                    catch { /* skip malformed */ }
                }
                else
                {
                    Match vm = oldValRe.Match(line);
                    if (vm.Success && lastEpoch > 0 && (points.Count == 0 || ((dynamic)points[^1]).epoch != lastEpoch))
                    {
                        points.Add(new
                        {
                            epoch = lastEpoch,
                            val   = double.Parse(vm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                            f0    = double.Parse(vm.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                            sty   = (double?)null,
                            diff  = (double?)null,
                            dur   = (double?)double.Parse(vm.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                        });
                    }
                    else if (line.TrimStart().StartsWith("── Paused", StringComparison.OrdinalIgnoreCase) && lastEpoch > 0)
                    {
                        pauses.Add(lastEpoch);
                    }
                }
            }
        }
        catch { /* file may be locked mid-write — return what we have */ }

        return Ok(new { points, pauses });
    }

    [HttpPost("training/resume")]
    public async Task<IActionResult> ResumeTraining([FromBody] ResumeRequest req)
    {
        if (!SafeNames.IsSafe(req.ModelName) || (req.Engine is not null && !SafeNames.IsSafe(req.Engine)))
            return BadRequest(new { error = "That name is not allowed." });
        if (string.IsNullOrWhiteSpace(req.ModelName))
            return BadRequest(new { error = "modelName is required." });
        if (string.IsNullOrEmpty(vsConfig.VoicesPath))
            return StatusCode(503, new { error = "VoiceSynthesis module is not configured." });
        if (voiceTraining?.IsSetupComplete != true)
            return StatusCode(503, new { error = "Voice module is still installing. Please wait." });

        string engine = req.Engine ?? "StyleTTS2";
        string settingsFile = Path.Combine(vsConfig.VoicesPath, engine, req.ModelName, "training.json");
        if (!System.IO.File.Exists(settingsFile))
            return NotFound(new { error = $"No saved training settings found for '{req.ModelName}'." });

        TrainingSettings? settings;
        try { settings = JsonSerializer.Deserialize<TrainingSettings>(System.IO.File.ReadAllText(settingsFile)); }
        catch { return StatusCode(500, new { error = "Could not parse training settings." }); }
        if (settings is null)
            return StatusCode(500, new { error = "Could not parse training settings." });

        int effectiveEpochs      = req.Epochs      ?? settings.Epochs;
        int effectiveSaveEveryN  = req.SaveEveryNEpochs ?? settings.SaveEveryNEpochs;

        // Persist updated targets so a future resume picks them up
        if (req.Epochs.HasValue || req.SaveEveryNEpochs.HasValue)
        {
            TrainingSettings updated = settings with { Epochs = effectiveEpochs, SaveEveryNEpochs = effectiveSaveEveryN };
            System.IO.File.WriteAllText(settingsFile, JsonSerializer.Serialize(updated));
        }

        if (req.Retrain)
        {
            string modelDir = Path.Combine(vsConfig.VoicesPath, engine, req.ModelName);
            string modelPth = Path.Combine(modelDir, "model.pth");
            string checkpointsDir = Path.Combine(modelDir, "Checkpoints");
            string statsLog = Path.Combine(modelDir, "Train.log");
            string debugLog = Path.Combine(modelDir, "train_debug.log");
            string tensorboardDir = Path.Combine(modelDir, "tensorboard");
            if (System.IO.File.Exists(modelPth)) System.IO.File.Delete(modelPth);
            if (Directory.Exists(checkpointsDir)) Directory.Delete(checkpointsDir, recursive: true);
            if (System.IO.File.Exists(statsLog)) System.IO.File.Delete(statsLog);
            if (System.IO.File.Exists(debugLog)) System.IO.File.Delete(debugLog);
            if (Directory.Exists(tensorboardDir)) Directory.Delete(tensorboardDir, recursive: true);
            logger.LogInformation("[Voice] Retrain requested — cleared checkpoints, train log, and tensorboard history for '{ModelName}' (dataset and epoch target retained)", req.ModelName);
        }

        TrainingJob job;
        try
        {
            int port = engine switch { "IndexTTS" => 8026, "KokoroRVC" => 8027, _ => 8021 };
            await EnsureEngineServer(engine, port, Path.Combine(vsConfig.VoicesPath, engine, settings.ModelName));
            VoiceModuleTrainer trainer = new(
                baseUrl:      $"http://localhost:{port}",
                audioPath:    settings.AudioPath,
                voiceDir:     Path.Combine(vsConfig.VoicesPath, engine, settings.ModelName),
                modelName:    settings.ModelName,
                epochs:       effectiveEpochs,
                saveEvery:    effectiveSaveEveryN,
                retrain:      req.Retrain,
                transcripts:  settings.Transcripts,
                phonemeSubs:  PhonemeSubstitutions.Path,
                logger:       logger);

            job = voiceTraining!.Start(trainer, settings.ModelName, lifetime.ApplicationStopping);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        logger.LogInformation(
            "[Voice] Resume training started — model: {ModelName}, epochs: {Epochs}",
            settings.ModelName, effectiveEpochs);

        string modelName = settings.ModelName;
        _ = Task.Run(async () =>
        {
            while (job.IsRunning) await Task.Delay(2000);
            if (job.IsSuccess)
            {
                logger.LogInformation("[Voice] Voice Synthesis of {ModelName} complete", modelName);
                if (Modules.Llm is not null)
                    await Modules.Llm.NotifyOwner($"> Voice Synthesis of {modelName} complete");
            }
            else
            {
                logger.LogWarning("[Voice] Training failed for {ModelName}: {Error}", modelName, job.Error);
            }
        });

        return Ok(new { jobId = job.JobId, modelName = job.ModelName });
    }

    // ── Audio Transcription ────────────────────────────────────────────────
    // Chunks uploaded audio and transcribes with Whisper so users can review/edit
    // transcripts before training any voice engine.

    [HttpPost("dataset/transcribe")]
    public IActionResult StartTranscription([FromBody] DatasetProcessRequest req)
    {
        if (!SafeNames.IsSafe(req.StageId))
            return BadRequest(new { error = "stageId is required." });

        string stageDir = Path.Combine(StagingRoot, req.StageId);
        if (!Directory.Exists(stageDir))
            return BadRequest(new { error = "Unknown stageId. Make one with POST /admin/voice/uploads first." });
        if (voiceTraining?.IsSetupComplete != true)
            return StatusCode(503, new { error = "StyleTTS2 must be set up first (Whisper is part of its environment)." });

        try
        {
            AudioTranscriber.Start(stageDir, vsConfig.DataDir, logger, lifetime.ApplicationStopping);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        return Ok(new { ok = true, started = true });
    }

    [HttpGet("dataset/transcribe/status")]
    public IActionResult TranscriptionStatus()
    {
        AudioTranscriber? t = AudioTranscriber.Current;
        if (t is null)
            return Ok(new { step = "Idle", percent = 0, running = false, clips = Array.Empty<object>() });

        return Ok(new
        {
            step    = t.Step,
            percent = t.Percent,
            running = t.IsRunning,
            error   = t.Error,
            workDir = t.WorkDir,
            clips   = t.Clips.Select(c => new { c.FileName, c.Transcript, c.Duration }),
        });
    }

    [HttpGet("dataset/transcribe/audio")]
    public IActionResult TranscribeAudio([FromQuery] string name)
    {
        AudioTranscriber? t = AudioTranscriber.Current;
        if (t is null)
            return NotFound(new { error = "No transcription in progress." });

        string wavPath = Path.Combine(t.WorkDir, "wavs", name);
        if (!System.IO.File.Exists(wavPath))
            return NotFound(new { error = $"Clip not found: {name}" });

        return PhysicalFile(wavPath, "audio/wav");
    }

    // ── Dataset Builder ───────────────────────────────────────────────────────
    // Processes uploaded clips (demucs + Whisper, split into parts) so the panel can
    // A/B the original against the cleaned variant and download a chosen dataset.

    [HttpPost("dataset/process")]
    public IActionResult ProcessDataset([FromBody] DatasetProcessRequest req)
    {
        if (!SafeNames.IsSafe(req.StageId))
            return BadRequest(new { error = "stageId is required." });

        string stageDir = Path.Combine(StagingRoot, req.StageId);
        if (!Directory.Exists(stageDir))
            return BadRequest(new { error = "Unknown stageId. Make one with POST /admin/voice/uploads first." });
        if (string.IsNullOrEmpty(vsConfig.StyleTtsPath))
            return StatusCode(503, new { error = "VoiceSynthesis module is not configured." });
        if (voiceTraining?.IsSetupComplete != true)
            return StatusCode(503, new { error = "StyleTTS2 is still installing. Please wait." });

        DatasetBuilder builder;
        try { builder = DatasetBuilder.Start(vsConfig.StyleTtsPath, vsConfig.DataDir, stageDir, !req.Demucs, logger, lifetime.ApplicationStopping); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }

        logger.LogInformation("[Voice] Dataset processing started for stage {StageId}", req.StageId);

        // Free RAM/GPU while demucs + Whisper run, then bring the llama servers back.
        _ = Task.Run(async () =>
        {
            if (llm is not null) await llm.StopAllServersAsync();
            while (builder.IsRunning) await Task.Delay(DATASET_POLL_MS);
            if (llm is not null) await llm.RestartAllServersAsync();
        });

        return Ok(new { ok = true, started = true });
    }

    [HttpGet("dataset/status")]
    public IActionResult DatasetStatus()
    {
        DatasetBuilder? builder = DatasetBuilder.Current;
        if (builder is null)
            return Ok(new { step = "Idle", percent = 0, running = false, parts = Array.Empty<object>() });

        return Ok(new
        {
            step      = builder.Step,
            percent   = builder.Percent,
            running   = builder.IsRunning,
            success   = builder.IsSuccess,
            error     = builder.Error,
            parts     = builder.Parts,
            hasDemucs = builder.HasDemucs,
        });
    }

    [HttpGet("dataset/audio")]
    public IActionResult DatasetAudio([FromQuery] string name, [FromQuery] string variant)
    {
        string? path = DatasetBuilder.Current?.VariantPath(name, variant);
        return path is null ? NotFound() : PhysicalFile(path, "audio/wav");
    }

    [HttpPost("dataset/split")]
    public IActionResult SplitClip([FromBody] DatasetSplitRequest req)
    {
        DatasetBuilder? builder = DatasetBuilder.Current;
        if (builder is null || builder.IsRunning)
            return BadRequest(new { error = "No completed dataset." });
        if (!SafeNames.IsSafe(req.Name)) return BadRequest(new { error = "That name is not allowed." });
        try
        {
            List<DatasetPart> parts = builder.Split(req.Name);
            return Ok(new { parts });
        }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    [HttpPost("dataset/unsplit")]
    public IActionResult UnsplitClip([FromBody] DatasetSplitRequest req)
    {
        DatasetBuilder? builder = DatasetBuilder.Current;
        if (builder is null || builder.IsRunning)
            return BadRequest(new { error = "No completed dataset." });
        if (!SafeNames.IsSafe(req.Name)) return BadRequest(new { error = "That name is not allowed." });
        try
        {
            DatasetPart? part = builder.Unsplit(req.Name);
            return Ok(new { part });
        }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    [HttpPost("dataset/build")]
    public IActionResult BuildDataset([FromBody] DatasetBuildRequest req)
    {
        DatasetBuilder? builder = DatasetBuilder.Current;
        if (builder is null || builder.IsRunning)
            return BadRequest(new { error = "No completed dataset to build." });

        return File(builder.BuildZip(req.Selections), "application/zip", "dataset.zip");
    }
}

public record TrainRequest(
    string ModelName,
    string StagingPath,
    string Engine          = "StyleTTS2",
    int    Epochs          = 365,
    int    SaveEveryNEpochs = 10,
    string QuantType       = "q4_k_m",
    Dictionary<string, string>? Transcripts = null);

/// <summary>One of ARI's optional subsystems, as shown on the control panel's Modules tab.</summary>
/// <param name="Key">Key under "Modules" in AriConfig.json.</param>
/// <param name="Name">Human-readable name.</param>
/// <param name="Description">What the module does, in plain English.</param>
/// <param name="Running">Whether it is actually live in this process right now.</param>
/// <param name="Required">Modules that cannot be turned off — switching them off would
/// take the control panel itself away, leaving no way to turn them back on.</param>

public record SpeakRequest(string Text, string? CheckpointPath = null,
    int DiffusionSteps = 5, float Alpha = 0.3f, float Beta = 0.7f, float EmbeddingScale = 1.0f,
    float Speed = 1.0f, float PauseScale = 1.0f);
public record SwitchEngineRequest(string Engine, string ModelName);
public record RenameModelRequest(string? NewName);
public record SplitSentencesRequest(string Text);
public record ResumeRequest(string ModelName, string? Engine = null, int? Epochs = null, int? SaveEveryNEpochs = null, bool Retrain = false);
public record DatasetProcessRequest(string StageId, bool Demucs = true);
public record DatasetBuildRequest(ARI.VoiceSynthesis.DatasetBuildSelection[] Selections);

public record DatasetSplitRequest(string Name);

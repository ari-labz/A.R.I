using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

public record TrainingProgress(string Step, int Percent, string? Detail = null);

/// <summary>
/// Trains an RVC voice model from raw audio files.
/// Drives the Python training scripts directly (no Gradio UI needed).
/// Outputs a final .pth + .index pair to voicesPath when complete.
/// </summary>
public class VoiceTrainer
{
    private const string Python = "/opt/homebrew/bin/python3.11";

    private readonly string rvcPath;
    private readonly string voicesPath;
    private readonly string trainingAudioPath;
    private readonly string modelName;
    private readonly int epochs;
    private readonly int batchSize;
    private readonly int saveFrequency;
    private readonly bool startFresh;
    private readonly ILogger? logger;

    public VoiceTrainer(
        string rvcPath,
        string voicesPath,
        string trainingAudioPath,
        string modelName,
        int epochs        = 100,
        int batchSize     = 4,
        int saveFrequency = 10,
        bool startFresh   = false,
        ILogger? logger   = null)
    {
        this.rvcPath            = rvcPath;
        this.voicesPath         = voicesPath;
        this.trainingAudioPath  = trainingAudioPath;
        this.modelName          = modelName;
        this.epochs             = epochs;
        this.batchSize          = batchSize;
        this.saveFrequency      = saveFrequency;
        this.startFresh         = startFresh;
        this.logger             = logger;
    }

    /// <summary>
    /// Runs the full training pipeline. Returns paths to the final .pth and .index files.
    /// Errors from RVC are surfaced as exceptions with full context.
    /// </summary>
    public async Task<(string PthPath, string IndexPath)> TrainAsync(
        IProgress<TrainingProgress>? progress = null,
        CancellationToken ct = default)
    {
        string expDir = Path.Combine(rvcPath, "logs", modelName);
        bool resuming = Directory.Exists(expDir) && !startFresh;

        if (Directory.Exists(expDir) && startFresh)
        {
            Log("Start fresh requested — deleting existing experiment directory.");
            Directory.Delete(expDir, recursive: true);

            string weightsDir = Path.Combine(rvcPath, "assets", "weights");
            foreach (string stale in Directory.Exists(weightsDir)
                ? Directory.GetFiles(weightsDir, $"{modelName}*.pth")
                : Array.Empty<string>())
            {
                File.Delete(stale);
                Log($"Removed stale weight: {Path.GetFileName(stale)}");
            }
        }
        else if (resuming)
        {
            Log("Existing checkpoints found — resuming training.");
        }

        Directory.CreateDirectory(expDir);

        if (!resuming)
        {
            // 1 — Preprocess audio
            await RunWithLog(
                script:   Path.Combine(rvcPath, "infer/modules/train/preprocess.py"),
                args:     $"\"{trainingAudioPath}\" 40000 2 \"{expDir}\" 0 3.0",
                logFile:  Path.Combine(expDir, "preprocess.log"),
                stepName: "Preprocessing audio",
                progress, ct);

            // 2a — Extract F0 (pitch)
            await RunWithLog(
                script:   Path.Combine(rvcPath, "infer/modules/train/extract/extract_f0_print.py"),
                args:     $"\"{expDir}\" 2 rmvpe",
                logFile:  Path.Combine(expDir, "extract_f0_feature.log"),
                stepName: "Extracting pitch",
                progress, ct);

            // 2b — Extract HuBERT features
            await RunWithLog(
                script:   Path.Combine(rvcPath, "infer/modules/train/extract_feature_print.py"),
                args:     $"cpu 1 0 0 \"{expDir}\" v2 False",
                logFile:  Path.Combine(expDir, "extract_f0_feature.log"),
                stepName: "Extracting features",
                progress, ct,
                clearLog: false);

            // 3 — Generate filelist + config.json for train.py
            GenerateFilelist(expDir);
        }
        else
        {
            progress?.Report(new TrainingProgress("Resuming from checkpoint", 0));
        }

        WriteTrainConfig(expDir);

        // 4 — Train model (tracks epoch progress via train.log)
        await RunTraining(expDir, progress, ct);

        // 5 — Build FAISS index (via embedded Python script)
        string indexPath = await BuildIndex(expDir, progress, ct);

        // 6 — Copy final outputs to Voices/
        string pthPath = CopyPth();

        string destIndex = Path.Combine(voicesPath, $"{modelName}.index");
        File.Copy(indexPath, destIndex, overwrite: true);
        Log($"Copied index → {destIndex}");

        progress?.Report(new TrainingProgress("Complete", 100));
        return (pthPath, destIndex);
    }

    // ── Training helpers ──────────────────────────────────────────────────────

    private void WriteTrainConfig(string expDir)
    {
        string dest = Path.Combine(expDir, "config.json");
        if (File.Exists(dest)) return; // already present (resume case)

        // RVC v2 ships 48k and 32k configs only — there's no v2/40k.json on disk.
        // Construct it: v2 model architecture (from 48k) + 40k data parameters.
        // hop_length=400 and upsample_rates=[10,10,2,2] give 10*10*2*2=400 = hop_length. ✓
        const string config = """
{
  "train": {
    "log_interval": 200,
    "seed": 1234,
    "epochs": 20000,
    "learning_rate": 1e-4,
    "betas": [0.8, 0.99],
    "eps": 1e-9,
    "batch_size": 4,
    "fp16_run": false,
    "lr_decay": 0.999875,
    "segment_size": 12800,
    "init_lr_ratio": 1,
    "warmup_epochs": 0,
    "c_mel": 45,
    "c_kl": 1.0
  },
  "data": {
    "max_wav_value": 32768.0,
    "sampling_rate": 40000,
    "filter_length": 2048,
    "hop_length": 400,
    "win_length": 2048,
    "n_mel_channels": 125,
    "mel_fmin": 0.0,
    "mel_fmax": null
  },
  "model": {
    "inter_channels": 192,
    "hidden_channels": 192,
    "filter_channels": 768,
    "n_heads": 2,
    "n_layers": 6,
    "kernel_size": 3,
    "p_dropout": 0,
    "resblock": "1",
    "resblock_kernel_sizes": [3, 7, 11],
    "resblock_dilation_sizes": [[1, 3, 5], [1, 3, 5], [1, 3, 5]],
    "upsample_rates": [10, 10, 2, 2],
    "upsample_initial_channel": 512,
    "upsample_kernel_sizes": [16, 16, 4, 4],
    "use_spectral_norm": false,
    "gin_channels": 256,
    "spk_embed_dim": 109
  }
}
""";
        File.WriteAllText(dest, config);
        Log("Wrote train config (v2/40k, derived).");
    }

    private void GenerateFilelist(string expDir)
    {
        string gtWavs   = Path.Combine(expDir, "0_gt_wavs");
        string features = Path.Combine(expDir, "3_feature768");
        string f0       = Path.Combine(expDir, "2a_f0");
        string f0nsf    = Path.Combine(expDir, "2b-f0nsf");

        if (!Directory.Exists(gtWavs))
            throw new DirectoryNotFoundException(
                $"Preprocessing output missing — expected {gtWavs}. Preprocessing may have failed.");

        static HashSet<string> Names(string dir, string suffix) =>
            new(Directory.GetFiles(dir)
                .Select(f => Path.GetFileName(f).Replace(suffix, "", StringComparison.Ordinal)));

        var gtNames   = Names(gtWavs,   ".wav");
        var featNames = Names(features, ".npy");
        var f0Names   = Names(f0,       ".wav.npy");
        var f0nsfNames= Names(f0nsf,    ".wav.npy");

        var names = gtNames.Intersect(featNames).Intersect(f0Names).Intersect(f0nsfNames).ToList();
        if (names.Count == 0)
            throw new InvalidOperationException(
                "Filelist is empty — no audio files matched across all extraction outputs. " +
                "Check preprocessing and feature extraction logs for errors.");

        var lines = names.Select(n =>
            $"{gtWavs}/{n}.wav|{features}/{n}.npy|{f0}/{n}.wav.npy|{f0nsf}/{n}.wav.npy|0");

        File.WriteAllLines(Path.Combine(expDir, "filelist.txt"), lines);
        Log($"Filelist: {names.Count} matched entries.");
    }

    private async Task RunTraining(string expDir, IProgress<TrainingProgress>? progress, CancellationToken ct)
    {
        string trainLog  = Path.Combine(expDir, "train.log");
        File.WriteAllText(trainLog, ""); // clear

        string preG = Path.Combine(rvcPath, "assets/pretrained_v2/f0G40k.pth");
        string preD = Path.Combine(rvcPath, "assets/pretrained_v2/f0D40k.pth");

        // -sw 1 = save small model at every checkpoint (and final) → assets/weights/{name}.pth
        string scriptArgs =
            $"\"{Path.Combine(rvcPath, "infer/modules/train/train.py")}\"" +
            $" -e \"{modelName}\" -sr 40k -f0 1 -bs {batchSize}" +
            $" -te {epochs} -se {saveFrequency}" +
            (File.Exists(preG) ? $" -pg \"{preG}\"" : "") +
            (File.Exists(preD) ? $" -pd \"{preD}\"" : "") +
            $" -l 0 -c 0 -sw 1 -v v2";

        Log($"Training: {epochs} epochs, batch size {batchSize}, save every {saveFrequency}");
        progress?.Report(new TrainingProgress("Training model", 0, $"0 / {epochs} epochs"));

        // RVC train.log format: "INFO:Voice:=====> Epoch: 25 [2026-06-07 17:32:13] | (0:01:39.635318)"
        var epochRegex = new Regex(@"Epoch:\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        await RunWithLogAndWatch(
            script:   null, // raw args passed directly
            fullArgs: $"{Python} {scriptArgs}",
            logFile:  trainLog,
            stepName: "Training model",
            progress, ct,
            lineHandler: line =>
            {
                var m = epochRegex.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int epoch))
                {
                    int pct = Math.Clamp(epoch * 100 / epochs, 0, 99);
                    progress?.Report(new TrainingProgress("Training model", pct, $"{epoch} / {epochs} epochs"));
                }
            });
    }

    private async Task<string> BuildIndex(string expDir, IProgress<TrainingProgress>? progress, CancellationToken ct)
    {
        Log("Building FAISS index...");
        progress?.Report(new TrainingProgress("Building index", 0));

        // Write an inline Python script — faiss is complex to replicate in C#
        string script = Path.Combine(Path.GetTempPath(), $"ari_build_index_{modelName}.py");
        File.WriteAllText(script, BuildIndexScript);

        string indexPath = "";
        string logFile   = Path.Combine(expDir, "build_index.log");

        await RunWithLogAndWatch(
            script:   null,
            fullArgs: $"{Python} \"{script}\" \"{rvcPath}\" \"{modelName}\"",
            logFile:  logFile,
            stepName: "Building index",
            progress, ct,
            lineHandler: line =>
            {
                // The script prints "INDEX_PATH:<path>" when done
                if (line.StartsWith("INDEX_PATH:", StringComparison.Ordinal))
                    indexPath = line["INDEX_PATH:".Length..].Trim();
            });

        try { File.Delete(script); } catch { /* best-effort */ }

        if (string.IsNullOrEmpty(indexPath) || !File.Exists(indexPath))
            throw new FileNotFoundException(
                $"Index build completed but output file not found. Check {logFile}");

        progress?.Report(new TrainingProgress("Building index", 100));
        return indexPath;
    }

    private string CopyPth()
    {
        // train.py with -sw 1 saves the FINAL epoch as assets/weights/{modelName}.pth
        string src  = Path.Combine(rvcPath, "assets", "weights", $"{modelName}.pth");
        if (!File.Exists(src))
        {
            // Fallback: pick the newest {modelName}_e*_s*.pth
            string weightsDir = Path.Combine(rvcPath, "assets", "weights");
            var candidates = Directory.Exists(weightsDir)
                ? Directory.GetFiles(weightsDir, $"{modelName}*.pth")
                    .OrderByDescending(File.GetLastWriteTime).ToArray()
                : Array.Empty<string>();

            if (candidates.Length == 0)
                throw new FileNotFoundException(
                    $"No trained model found for '{modelName}' in {weightsDir}. " +
                    "Check train.log for errors.");

            src = candidates[0];
        }

        Directory.CreateDirectory(voicesPath);
        string dest = Path.Combine(voicesPath, $"{modelName}.pth");
        File.Copy(src, dest, overwrite: true);
        Log($"Copied model → {dest}");
        return dest;
    }

    // ── Process runners ───────────────────────────────────────────────────────

    private async Task RunWithLog(
        string script, string args, string logFile, string stepName,
        IProgress<TrainingProgress>? progress, CancellationToken ct,
        bool clearLog = true)
    {
        if (clearLog && File.Exists(logFile))
            File.WriteAllText(logFile, "");

        Log($"Step: {stepName}");
        progress?.Report(new TrainingProgress(stepName, 0));

        await RunWithLogAndWatch(
            script:   script,
            fullArgs: null,
            logFile:  logFile,
            stepName: stepName,
            progress, ct,
            args:     args);

        progress?.Report(new TrainingProgress(stepName, 100));
    }

    private async Task RunWithLogAndWatch(
        string? script,
        string? fullArgs,
        string logFile,
        string stepName,
        IProgress<TrainingProgress>? progress,
        CancellationToken ct,
        string? args = null,
        Action<string>? lineHandler = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Open log for append so the process can also write to it via redirect
        string processArgs = fullArgs ?? $"{Python} \"{script}\" {args}";

        // Run log tail concurrently with the process
        var watchTask = TailLog(logFile, lineHandler, cts.Token);

        Exception? processError = null;
        try
        {
            await RunProcess(processArgs, logFile, ct);
        }
        catch (Exception ex)
        {
            processError = ex;
        }
        finally
        {
            await cts.CancelAsync();
        }

        try { await watchTask; } catch (OperationCanceledException) { }

        // Final flush — read any lines written after cancellation
        await FlushLog(logFile, lineHandler);

        if (processError is not null)
            throw new Exception($"[{stepName}] {processError.Message}", processError);
    }

    private async Task RunProcess(string fullArgs, string logFile, CancellationToken ct)
    {
        // Parse executable + args (first token is the executable)
        string exe;
        string arguments;
        if (fullArgs.StartsWith('"'))
        {
            int close = fullArgs.IndexOf('"', 1);
            exe       = fullArgs[1..close];
            arguments = fullArgs[(close + 2)..].Trim();
        }
        else
        {
            int space = fullArgs.IndexOf(' ');
            exe       = space < 0 ? fullArgs : fullArgs[..space];
            arguments = space < 0 ? "" : fullArgs[(space + 1)..].Trim();
        }

        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = arguments,
            WorkingDirectory       = rvcPath,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        // CPU-only env vars — same as RVC launch in VoiceSynthesisService
        psi.Environment["OMP_NUM_THREADS"]             = "1";
        psi.Environment["MKL_NUM_THREADS"]             = "1";
        psi.Environment["OPENBLAS_NUM_THREADS"]        = "1";
        psi.Environment["PYTORCH_ENABLE_MPS_FALLBACK"] = "1";
        psi.Environment["RVC_FORCE_CPU"]               = "1";

        using var proc = Process.Start(psi)
            ?? throw new Exception($"Failed to start process: {exe}");

        // Redirect stdout/stderr to the step's log file so they appear in ARI.log
        using var logWriter = new StreamWriter(logFile, append: true);

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Log(e.Data);
            lock (logWriter) { logWriter.WriteLine(e.Data); logWriter.Flush(); }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Log(e.Data);
            lock (logWriter) { logWriter.WriteLine(e.Data); logWriter.Flush(); }
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new Exception(
                $"Process exited with code {proc.ExitCode}. " +
                $"Check {logFile} for details. Command: {exe} {arguments[..Math.Min(arguments.Length, 120)]}");
    }

    private async Task TailLog(string logFile, Action<string>? handler, CancellationToken ct)
    {
        long pos = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(400, ct); } catch (OperationCanceledException) { break; }
            pos = await ReadNewLines(logFile, pos, handler);
        }
    }

    private Task FlushLog(string logFile, Action<string>? handler) =>
        ReadNewLines(logFile, 0, handler).ContinueWith(_ => { });

    private async Task<long> ReadNewLines(string logFile, long pos, Action<string>? handler)
    {
        if (!File.Exists(logFile)) return pos;
        try
        {
            using var fs     = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            fs.Seek(pos, SeekOrigin.Begin);
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
                handler?.Invoke(line);
            return fs.Position;
        }
        catch { return pos; }
    }

    private void Log(string message) =>
        logger?.LogInformation("[VoiceTrainer/{Model}] {Message}", modelName, message);

    // ── Embedded index-build script ───────────────────────────────────────────

    private const string BuildIndexScript = """
import sys, os, traceback
import numpy as np

rvc_path   = sys.argv[1]
model_name = sys.argv[2]

os.chdir(rvc_path)
sys.path.insert(0, rvc_path)

try:
    import faiss
except ImportError:
    print("ERROR: faiss-cpu not installed. Run: pip install faiss-cpu")
    sys.exit(1)

exp_dir  = f"logs/{model_name}"
feat_dir = f"{exp_dir}/3_feature768"

if not os.path.isdir(feat_dir):
    print(f"ERROR: Feature directory not found: {feat_dir}")
    sys.exit(1)

npy_files = sorted(f for f in os.listdir(feat_dir) if f.endswith('.npy'))
if not npy_files:
    print(f"ERROR: No .npy feature files in {feat_dir}")
    sys.exit(1)

print(f"Loading {len(npy_files)} feature files...")
npys = [np.load(f"{feat_dir}/{n}") for n in npy_files]
big  = np.concatenate(npys, 0)
idx  = np.arange(big.shape[0]); np.random.shuffle(idx); big = big[idx]
print(f"Total features: {big.shape}")

n_ivf = min(int(16 * big.shape[0] ** 0.5), big.shape[0] // 39)
print(f"Building IVF index with {n_ivf} clusters...")

index = faiss.index_factory(768, f"IVF{n_ivf},Flat")
faiss.extract_index_ivf(index).nprobe = 1
index.train(big)

batch = 8192
for i in range(0, big.shape[0], batch):
    index.add(big[i:i+batch])

out = os.path.abspath(f"{exp_dir}/added_IVF{n_ivf}_Flat_nprobe_1_{model_name}_v2.index")
faiss.write_index(index, out)
print(f"INDEX_PATH:{out}")
""";
}

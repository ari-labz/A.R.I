using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

public record TrainingProgress(string Step, int Percent, string? Detail = null);
public record TrainingSettings(string AudioPath, string ModelName, int Epochs, int SaveEveryNEpochs);

// styleTtsPath is install content (StyleTTS2 source — Utils/, the training scripts, the shared
// Data/OOD_texts.txt scaffold); dataDir is AppDataRoot-based mutable state (venv, per-model
// training work dirs, the downloaded pretrained-checkpoint cache).
public class StyleTtsTrainer(
    string   styleTtsPath,
    string   dataDir,
    string   voicesPath,
    string   audioPath,
    string   modelName,
    int      epochs           = 500,
    int      saveEveryNEpochs = 5,
    ILogger? logger           = null)
{
    private const int    CHUNK_SECS   = 8;
    private const int    MIN_CHUNK_SECS = 2;

    // Pretrained LibriTTS model — downloaded automatically if missing
    private const string PRETRAINED_URL = "https://huggingface.co/yl4579/StyleTTS2-LibriTTS/resolve/main/Models/LibriTTS/epochs_2nd_00020.pth";

    public async Task<string> Train(IProgress<TrainingProgress>? progress = null, CancellationToken ct = default)
    {
        string workDir   = Path.Combine(dataDir, "Data", modelName);
        string audioDir  = Path.Combine(workDir, "wavs");
        string outputDir = Path.Combine(voicesPath, modelName);

        Directory.CreateDirectory(audioDir);
        Directory.CreateDirectory(outputDir);

        // Persist settings so the user can resume later without re-uploading.
        var settings = new TrainingSettings(audioPath, modelName, epochs, saveEveryNEpochs);
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, "training.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        string savedTrainList = Path.Combine(outputDir, "train_list.txt");
        string trainList      = Path.Combine(workDir, "train_list.txt");

        // A fresh upload always wins: rebuild from it so leftovers from a previous failed run
        // for the same model name can't poison the dataset. Reusing on-disk chunks is only a
        // fallback for resume, where the original upload is already gone.
        string? metadataPath = Directory.Exists(audioPath) ? PrepareDataset(audioPath) : null;
        bool    hasRawUpload = Directory.Exists(audioPath)
            ? Directory.GetFiles(audioPath, "*.wav").Length > 0
            : File.Exists(audioPath);

        if (metadataPath is not null)
        {
            // Prepared dataset from the Dataset Builder: clips are already split and transcribed,
            // so copy them straight in and skip both chunking and Whisper.
            ResetDir(audioDir);
            progress?.Report(new TrainingProgress("Preparing", 15, "Using prepared dataset (skipping split + transcription)"));
            BuildPreparedList(metadataPath, audioDir, trainList);
            await PhonemiseTrainList(trainList, ct);
            File.Copy(trainList, savedTrainList, overwrite: true);
        }
        else if (hasRawUpload)
        {
            ResetDir(audioDir);
            string[] sourceFiles = Directory.Exists(audioPath)
                ? Directory.GetFiles(audioPath, "*.wav")
                : new[] { audioPath };

            progress?.Report(new TrainingProgress("Chunking", 5, $"Splitting {sourceFiles.Length} file(s) into clips"));
            foreach (string source in sourceFiles)
                await ChunkAudio(source, audioDir, ct);

            progress?.Report(new TrainingProgress("Transcribing", 15, "Transcribing audio with Whisper"));
            trainList = await Transcribe(audioDir, workDir, ct);
            await PhonemiseTrainList(trainList, ct);
            File.Copy(trainList, savedTrainList, overwrite: true);
        }
        else
        {
            // Resume: original upload is gone — reuse the chunks + transcription already on disk.
            // savedTrainList is already phonemised from the original run — reuse untouched.
            string[] existingChunks = Directory.GetFiles(audioDir, "*.wav");
            if (existingChunks.Length == 0 || !File.Exists(savedTrainList))
                throw new FileNotFoundException($"No audio found to train '{modelName}'. Upload clips and try again.");
            progress?.Report(new TrainingProgress("Chunking", 5, $"Using {existingChunks.Length} existing clip(s)"));
            File.Copy(savedTrainList, trainList, overwrite: true);
            progress?.Report(new TrainingProgress("Transcribing", 15, "Using saved transcription"));
        }

        progress?.Report(new TrainingProgress("Preparing", 25, "Downloading pretrained model if needed"));
        string baseModel = await EnsurePretrainedModel(ct);

        // Resume from existing checkpoint if one exists for this model name
        string existingModel = Path.Combine(outputDir, "model.pth");
        string pretrainedModel = File.Exists(existingModel) ? existingModel : baseModel;
        if (pretrainedModel == existingModel)
            logger?.LogInformation("[StyleTTS2-Train] Resuming from existing checkpoint: {Path}", existingModel);

        bool isResume = pretrainedModel == existingModel;
        string configPath = WriteTrainingConfig(workDir, trainList, audioDir, outputDir, pretrainedModel, isResume);

        // Clean up any loose .pth files left over from a previous interrupted run
        MoveLooseCheckpoints(outputDir);

        progress?.Report(new TrainingProgress("Training", 0, $"Fine-tuning StyleTTS2 for {epochs} epochs"));
        await FineTune(configPath, outputDir, progress, ct);

        // Copy first training clip as reference audio for inference
        string[] trainingWavs = Directory.GetFiles(audioDir, "*.wav");
        if (trainingWavs.Length > 0)
            File.Copy(trainingWavs.OrderBy(f => f).First(), Path.Combine(outputDir, "reference.wav"), overwrite: true);

        string checkpoint = OrganiseCheckpoints(outputDir);
        progress?.Report(new TrainingProgress("Complete", 100, $"Model saved to {Path.GetFileName(checkpoint)}"));
        return checkpoint;
    }

    private async Task ChunkAudio(string source, string audioDir, CancellationToken ct)
    {
        string python     = Paths.StyleTts2Python;
        string scriptPath = Path.Combine(Path.GetTempPath(), "ari_chunk.py");
        await File.WriteAllTextAsync(scriptPath, BuildChunkScript(source, audioDir, CHUNK_SECS, MIN_CHUNK_SECS), ct);
        await RunPython(python, scriptPath, null, ct);
    }

    // Clears a directory so a fresh dataset never mixes with a previous run's leftover clips.
    private static void ResetDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
    }

    // Detects a Dataset Builder export in the staging dir: extracts any .zip, then returns the
    // metadata.csv path if present. Returns null for raw-audio uploads (the chunking path).
    private static string? PrepareDataset(string stagingDir)
    {
        if (!Directory.Exists(stagingDir)) return null;

        foreach (string zip in Directory.GetFiles(stagingDir, "*.zip"))
            ZipFile.ExtractToDirectory(zip, stagingDir, overwriteFiles: true);

        return Directory.GetFiles(stagingDir, "metadata.csv", SearchOption.AllDirectories).FirstOrDefault();
    }

    // Builds the training list straight from a prepared metadata.csv ("name.wav|transcript"),
    // copying each referenced clip into the training wavs/ folder. No chunking, no Whisper.
    private void BuildPreparedList(string metadataPath, string audioDir, string trainList)
    {
        string        datasetDir = Path.GetDirectoryName(metadataPath)!;
        StringBuilder list       = new();
        int           count      = 0;

        foreach (string line in File.ReadAllLines(metadataPath))
        {
            string[] columns = line.Split('|');
            if (columns.Length < 2) continue;
            string name       = Path.GetFileName(columns[0].Trim());
            string transcript = columns[1].Trim();
            if (name.Length == 0 || transcript.Length == 0) continue;

            string nested = Path.Combine(datasetDir, "wavs", name);
            string source = File.Exists(nested) ? nested : Path.Combine(datasetDir, name);
            if (!File.Exists(source)) continue;

            string dest = Path.Combine(audioDir, name);
            File.Copy(source, dest, overwrite: true);
            list.AppendLine($"{dest}|{transcript}|0");
            count++;
        }

        File.WriteAllText(trainList, list.ToString());
        logger?.LogInformation("[StyleTTS2-Train] Prepared dataset: {Count} clip(s)", count);
    }

    private async Task<string> Transcribe(string audioDir, string workDir, CancellationToken ct)
    {
        string whisper    = Paths.StyleTts2Whisper;
        string[] wavs     = Directory.GetFiles(audioDir, "*.wav");
        StringBuilder list = new();

        foreach (string wav in wavs)
        {
            string transcript = await RunWhisper(whisper, wav, ct);
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                string clean = transcript.Trim().ReplaceLineEndings(" ");
                clean = FixWhisperNames(clean);
                list.AppendLine($"{wav}|{clean}|0");
            }
        }

        string trainList = Path.Combine(workDir, "train_list.txt");
        await File.WriteAllTextAsync(trainList, list.ToString(), ct);
        logger?.LogInformation("[StyleTTS2-Train] Transcribed {Count} clips", wavs.Length);
        return trainList;
    }

    private async Task<string> EnsurePretrainedModel(CancellationToken ct)
    {
        string modelsDir = Path.Combine(dataDir, "Models", "LibriTTS");
        string modelPath = Path.Combine(modelsDir, "epochs_2nd_00020.pth");

        if (File.Exists(modelPath)) return modelPath;

        Directory.CreateDirectory(modelsDir);
        logger?.LogInformation("[StyleTTS2-Train] Downloading pretrained LibriTTS model...");

        string python     = Paths.StyleTts2Python;
        string scriptPath = Path.Combine(Path.GetTempPath(), "ari_dl_pretrained.py");
        string script =
            "from cached_path import cached_path\n" +
            $"p = cached_path('{PRETRAINED_URL}')\n" +
            $"import shutil; shutil.copy(p, r'{modelPath}')\n" +
            $"print(r'{modelPath}')\n";
        await File.WriteAllTextAsync(scriptPath, script, ct);
        await RunPython(python, scriptPath, null, ct);

        return modelPath;
    }

    private string WriteTrainingConfig(
        string workDir, string trainList, string audioDir, string outputDir, string pretrainedModel, bool isResume = false)
    {
        string configPath = Path.Combine(workDir, "config_ft.yml");

        string yaml = $"""
log_dir: "{outputDir}"
save_freq: {saveEveryNEpochs}
log_interval: 10
device: "{DetectDevice()}"
epochs: {epochs}
batch_size: 4
max_len: 400
pretrained_model: "{pretrainedModel}"
second_stage_load_pretrained: true
load_only_params: {(isResume ? "false" : "true")}

F0_path: "Utils/JDC/bst.t7"
ASR_config: "Utils/ASR/config.yml"
ASR_path: "Utils/ASR/epoch_00080.pth"
PLBERT_dir: "Utils/PLBERT/"

data_params:
  train_data: "{trainList}"
  val_data: "{trainList}"
  root_path: "{audioDir}"
  OOD_data: "Data/OOD_texts.txt"
  min_length: 50

preprocess_params:
  sr: 24000
  spect_params:
    n_fft: 2048
    win_length: 1200
    hop_length: 300

model_params:
  multispeaker: false
  dim_in: 64
  hidden_dim: 512
  max_conv_dim: 512
  n_layer: 3
  n_mels: 80
  n_token: 178
  max_dur: 50
  style_dim: 128
  dropout: 0.2
  decoder:
    type: 'hifigan'
    resblock_kernel_sizes: [3,7,11]
    upsample_rates: [10,5,3,2]
    upsample_initial_channel: 512
    resblock_dilation_sizes: [[1,3,5],[1,3,5],[1,3,5]]
    upsample_kernel_sizes: [20,10,6,4]
  slm:
    model: 'microsoft/wavlm-base-plus'
    sr: 16000
    hidden: 768
    nlayers: 13
    initial_channel: 64
  diffusion:
    embedding_mask_proba: 0.1
    transformer:
      num_layers: 3
      num_heads: 8
      head_features: 64
      multiplier: 2
    dist:
      sigma_data: 0.2
      estimate_sigma_data: true
      mean: -3.0
      std: 1.0

loss_params:
  lambda_mel: 5.
  lambda_gen: 1.
  lambda_slm: 0.
  lambda_mono: 1.
  lambda_s2s: 1.
  lambda_F0: 1.
  lambda_norm: 1.
  lambda_dur: 1.
  lambda_ce: 20.
  lambda_sty: 1.
  lambda_diff: 1.
  diff_epoch: 50
  joint_epoch: 150

optimizer_params:
  lr: 0.00005
  bert_lr: 0.000005
  ft_lr: 0.00005

slmadv_params:
  min_len: 400
  max_len: 500
  batch_percentage: 0.5
  iter: 10
  thresh: 5
  scale: 0.01
  sig: 1.5
""";

        File.WriteAllText(configPath, yaml);
        return configPath;
    }

    private async Task FineTune(
        string configPath,
        string outputDir,
        IProgress<TrainingProgress>? progress,
        CancellationToken ct)
    {
        string python      = Paths.StyleTts2Python;
        string trainScript = Path.Combine(Path.GetTempPath(), "ari_train_stt2.py");
        await File.WriteAllTextAsync(trainScript,
            // MPS fallback: ops not natively supported on Apple GPU fall back to CPU instead of crashing.
            "import os; os.environ.setdefault('PYTORCH_ENABLE_MPS_FALLBACK', '1')\n" +
            "import torch, sys\n" +
            $"sys.path.insert(0, r'{Path.GetFullPath(styleTtsPath)}')\n" +
            // PyTorch 2.6 changed torch.load default to weights_only=True — patch it back for StyleTTS2
            "_orig = torch.load\n" +
            "torch.load = lambda *a, **kw: _orig(*a, **{**kw, 'weights_only': False})\n" +
            // empty_cache: flush MPS allocator (cuda version is a no-op on MPS)
            "if torch.backends.mps.is_available():\n" +
            "    _orig_empty = torch.cuda.empty_cache\n" +
            "    torch.cuda.empty_cache = lambda: (torch.mps.empty_cache(), _orig_empty())\n" +
            // WavLM (microsoft/wavlm-base-plus) only supports CUDA/CPU, not MPS.
            // We patch train_finetune.WavLMLoss (its module-global) after import but before main() runs.
            // IMPORTANT: do NOT replace losses.WavLMLoss — the original __init__ does
            //   super(WavLMLoss, self).__init__() which looks up WavLMLoss by name in losses module.
            //   Replacing it there causes infinite recursion with missing args.
            // WavLM doesn't support MPS. Running it on CPU per-batch is too slow.
            // Replace with a no-op that returns zeros — lambda_slm is set to 0 in the config
            // so this loss term has no effect on training anyway.
            "if not torch.cuda.is_available():\n" +
            "    import torch.nn as _nn, losses as _losses\n" +
            "    class _CpuWavLMLoss(_nn.Module):\n" +
            "        def __init__(self, *a, **kw): super().__init__()\n" +
            "        def to(self, *a, **kw): return self\n" +
            "        def forward(self, wav, y_rec): return torch.tensor(0.0, device=wav.device)\n" +
            "        def generator(self, y_rec): return torch.tensor(0.0, device=y_rec.device)\n" +
            "        def discriminator(self, wav, y_rec): return torch.tensor(0.0, device=wav.device)\n" +
            "        def discriminator_forward(self, wav): return torch.zeros(wav.shape[0], 1, device=wav.device)\n" +
            "    import train_finetune as _tf\n" +
            "    _tf.WavLMLoss = _CpuWavLMLoss\n" +
            $"sys.argv = ['train_finetune.py', '-p', r'{configPath}']\n" +
            "from train_finetune import main\n" +
            "main()\n", ct);

        ProcessStartInfo info = new()
        {
            FileName               = python,
            Arguments              = $"\"{trainScript}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = styleTtsPath,
        };

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start StyleTTS2 training process.");

        // Kill the entire process tree when the token is cancelled so PyTorch doesn't
        // outlive ARI as an orphan and consume all system memory.
        using CancellationTokenRegistration killReg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        // Move epoch .pth files into Checkpoints/ as they're created so the voice
        // directory never accumulates loose checkpoint files during a long training run.
        using var cleanupCts = new CancellationTokenSource();
        Task cleanupTask = Task.Run(async () =>
        {
            while (!cleanupCts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(30_000, cleanupCts.Token); }
                catch (OperationCanceledException) { break; }
                MoveLooseCheckpoints(outputDir);
            }
        }, CancellationToken.None);

        Task stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(CancellationToken.None)) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    logger?.LogInformation("[StyleTTS2-Train] {Line}", line);
                TrainingProgress? update = ParseProgress(line, epochs);
                if (update != null)
                    progress?.Report(update);
            }
        }, CancellationToken.None);

        var stderrLines = new System.Text.StringBuilder();
        Task stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                stderrLines.AppendLine(line);
                logger?.LogWarning("[StyleTTS2-Train] [stderr] {Line}", line);
            }
        }, CancellationToken.None);

        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdoutTask, stderrTask);

        await cleanupCts.CancelAsync();
        await cleanupTask.ConfigureAwait(false);

        logger?.LogInformation("[StyleTTS2-Train] Process exited with code {Code}", process.ExitCode);

        if (process.ExitCode != 0 && !ct.IsCancellationRequested)
            throw new Exception($"StyleTTS2 training failed:\n{stderrLines}");
    }

    // Moves any loose epoch_2nd_*.pth files from outputDir into Checkpoints/ subfolders
    // and updates model.pth to the highest-epoch checkpoint found. Safe to call at any
    // time — skips files still being written and is a no-op when nothing is loose.
    private static void MoveLooseCheckpoints(string outputDir)
    {
        string[] loose = [
            ..Directory.GetFiles(outputDir, "epoch_2nd_*.pth"),
            ..Directory.GetFiles(outputDir, "epoch_*.pth").Where(f => !f.Contains("epoch_2nd_")),
        ];
        if (loose.Length == 0) return;

        string checkpointsDir = Path.Combine(outputDir, "Checkpoints");
        Directory.CreateDirectory(checkpointsDir);

        foreach (string pth in loose)
        {
            string fname     = Path.GetFileNameWithoutExtension(pth);
            string numPart   = fname.Split('_').Last();
            int humanEpoch   = int.TryParse(numPart, out int idx) ? idx + 1 : 0;
            string epochDir  = Path.Combine(checkpointsDir, $"{humanEpoch}_epochs");
            Directory.CreateDirectory(epochDir);
            string dest = Path.Combine(epochDir, Path.GetFileName(pth));
            try
            {
                // Skip if the file is still open for writing by Python
                using (new FileStream(pth, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Move(pth, dest, overwrite: true);
            }
            catch { /* will retry on next cycle */ }
        }

        UpdateModelPth(outputDir, checkpointsDir);
    }

    // Moves any remaining loose checkpoints and sets model.pth to the highest-epoch
    // checkpoint across all Checkpoints/ subfolders.
    private static string OrganiseCheckpoints(string outputDir)
    {
        MoveLooseCheckpoints(outputDir);

        string checkpointsDir = Path.Combine(outputDir, "Checkpoints");
        string[] all = Directory.GetFiles(checkpointsDir, "*.pth", SearchOption.AllDirectories);
        if (all.Length == 0)
            throw new FileNotFoundException($"No checkpoint found in {checkpointsDir}");

        UpdateModelPth(outputDir, checkpointsDir);

        // Promote config so the voice module finds config.yml
        string configFt = Path.Combine(outputDir, "config_ft.yml");
        if (File.Exists(configFt))
            File.Copy(configFt, Path.Combine(outputDir, "config.yml"), overwrite: true);

        return Path.Combine(outputDir, "model.pth");
    }

    // Copies the highest-epoch checkpoint in Checkpoints/ to model.pth.
    private static void UpdateModelPth(string outputDir, string checkpointsDir)
    {
        string[] all = Directory.GetFiles(checkpointsDir, "*.pth", SearchOption.AllDirectories);
        if (all.Length == 0) return;

        // Pick highest epoch by the numeric suffix in the filename (not file time,
        // which can be wrong after moves or on resumed runs).
        string best = all.OrderByDescending(f =>
        {
            string num = Path.GetFileNameWithoutExtension(f).Split('_').Last();
            return int.TryParse(num, out int n) ? n : 0;
        }).First();

        File.Copy(best, Path.Combine(outputDir, "model.pth"), overwrite: true);
    }

    // Rewrites column 2 (transcript) of train_list.txt with IPA phonemes via the SAME shared
    // phonemizer the inference server uses (ari_phonemize), so training and inference feed the
    // model an identical token alphabet. Without this the model learns graphemes but hears IPA
    // at speak-time and can only approximate (mispronunciations, wrong pitch).
    private async Task PhonemiseTrainList(string trainList, CancellationToken ct)
    {
        string python     = Paths.StyleTts2Python;
        string scriptPath = Path.Combine(Path.GetTempPath(), "ari_phonemise_list.py");
        string repoRoot   = Path.GetFullPath(styleTtsPath);
        string listPath   = Path.GetFullPath(trainList);
        // ARI's overrides live in the ARI project; pass the path so the phonemizer loads it in place.
        string? subsPath  = PhonemeSubstitutions.Path;
        string configureLine = subsPath is null ? "" : $"_ph.configure(r'{subsPath}')\n";
        string script =
            "import sys\n" +
            $"sys.path.insert(0, r'{repoRoot}')\n" +
            "import phonemize as _ph\n" +
            configureLine +
            "from phonemize import preprocess, phonemize\n" +
            $"path = r'{listPath}'\n" +
            "out = []\n" +
            "with open(path, encoding='utf-8') as f:\n" +
            "    for line in f:\n" +
            "        line = line.rstrip('\\n')\n" +
            "        if not line.strip(): continue\n" +
            "        parts = line.split('|')\n" +
            "        if len(parts) < 3:\n" +
            "            out.append(line); continue\n" +
            "        parts[1] = phonemize(preprocess(parts[1])).replace('|', ' ')\n" +
            "        out.append('|'.join(parts))\n" +
            "with open(path, 'w', encoding='utf-8') as f:\n" +
            "    f.write('\\n'.join(out) + '\\n')\n" +
            "print(f'phonemised {len(out)} lines')\n";
        await File.WriteAllTextAsync(scriptPath, script, ct);
        logger?.LogInformation("[StyleTTS2-Train] Phonemising train list (IPA parity with inference)");
        await RunPython(python, scriptPath, null, ct);
    }

    private async Task RunPython(string python, string scriptPath, string? workDir, CancellationToken ct)
    {
        ProcessStartInfo info = new()
        {
            FileName               = python,
            Arguments              = $"\"{scriptPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        if (workDir != null) info.WorkingDirectory = workDir;

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start Python.");

        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new Exception($"Python script failed: {stderr}");
    }

    private async Task<string> RunWhisper(string whisper, string wavFile, CancellationToken ct)
    {
        string outDir = Path.GetDirectoryName(wavFile)!;
        ProcessStartInfo info = new()
        {
            FileName               = whisper,
            Arguments              = $"\"{wavFile}\" --model base.en --output_format txt --output_dir \"{outDir}\" --fp16 False --condition_on_previous_text False",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start Whisper.");

        await process.WaitForExitAsync(ct);

        string txtFile = Path.ChangeExtension(wavFile, ".txt");
        return File.Exists(txtFile) ? await File.ReadAllTextAsync(txtFile, ct) : "";
    }

    // Optional corrections for names Whisper reliably mis-transcribes in your training clips.
    // Populate with (wrong, right) pairs for your own voice/name if needed; empty by default.
    private static readonly (string Wrong, string Right)[] NameFixes = [];

    private static string FixWhisperNames(string text)
    {
        foreach (var (wrong, right) in NameFixes)
            text = text.Replace(wrong, right, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static TrainingProgress? ParseProgress(string line, int totalEpochs)
    {
        // train_finetune.py prints: "Epochs: 5"
        if (!line.StartsWith("Epochs:", StringComparison.OrdinalIgnoreCase)) return null;
        string numStr = line["Epochs:".Length..].Trim();
        if (!int.TryParse(numStr, out int current)) return null;
        int percent = (int)((double)current / totalEpochs * 99);
        return new TrainingProgress("Training", Math.Min(percent, 99), $"Epoch {current}/{totalEpochs}");
    }

    private static string DetectDevice()
    {
        // Checked at trainer construction time on the host machine.
        // macOS: train on CPU, not MPS. Apple's MPS backend has flush-to-zero / broken-kernel
        // bugs (esp. torch.stft and weight/spectral-norm) that drive training to NaN; CPU is
        // numerically reliable. train_finetune.py maxes out CPU threads to compensate.
        if (OperatingSystem.IsMacOS()) return "cpu";
        return "cuda";
    }

    private static string BuildChunkScript(string source, string outDir, int chunkSecs, int minChunkSecs)
    {
        // Prefix chunks with the source filename so multiple uploaded files don't overwrite
        // each other's chunk_0000.wav in the shared wavs/ folder.
        string stem = new string(Path.GetFileNameWithoutExtension(source)
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return
            "import soundfile as sf, numpy as np, os, torch, torchaudio\n" +
            $"data, sr = sf.read(r'{source}')\n" +
            "if data.ndim > 1:\n" +
            "    data = data.mean(axis=1)\n" +
            "if sr != 24000:\n" +
            "    t = torch.tensor(data).unsqueeze(0).float()\n" +
            "    t = torchaudio.functional.resample(t, sr, 24000)\n" +
            "    data = t.squeeze(0).numpy(); sr = 24000\n" +
            $"chunk_samples = sr * {chunkSecs}\n" +
            "for i, start in enumerate(range(0, len(data), chunk_samples)):\n" +
            "    chunk = data[start:start + chunk_samples]\n" +
            $"    if len(chunk) < sr * {minChunkSecs}: continue\n" +
            $"    out = os.path.join(r'{outDir}', f'chunk_{stem}_{{i:04d}}.wav')\n" +
            "    sf.write(out, chunk, sr)\n";
    }
}

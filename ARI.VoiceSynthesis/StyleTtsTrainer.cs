using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

public record TrainingProgress(string Step, int Percent, string? Detail = null);

public class StyleTtsTrainer(
    string   styleTtsPath,
    string   voicesPath,
    string   audioPath,
    string   modelName,
    int      epochs           = 50,
    int      saveEveryNEpochs = 5,
    ILogger? logger           = null)
{
    private static string VenvPython  => OperatingSystem.IsWindows() ? @"venv\Scripts\python.exe" : "venv/bin/python";
    private static string VenvWhisper => OperatingSystem.IsWindows() ? @"venv\Scripts\whisper.exe" : "venv/bin/whisper";
    private const int    CHUNK_SECS   = 15;

    // Pretrained LibriTTS model — downloaded automatically if missing
    private const string PRETRAINED_URL = "https://huggingface.co/yl4579/StyleTTS2-LibriTTS/resolve/main/Models/LibriTTS/epochs_2nd_00020.pth";

    public async Task<string> Train(IProgress<TrainingProgress>? progress = null, CancellationToken ct = default)
    {
        string workDir   = Path.Combine(styleTtsPath, "Data", modelName);
        string audioDir  = Path.Combine(workDir, "wavs");
        string outputDir = Path.Combine(voicesPath, modelName);

        Directory.CreateDirectory(audioDir);
        Directory.CreateDirectory(outputDir);

        string[] sourceFiles = Directory.Exists(audioPath)
            ? Directory.GetFiles(audioPath, "*.wav")
            : new[] { audioPath };

        if (sourceFiles.Length == 0)
            throw new FileNotFoundException($"No WAV files found in {audioPath}");

        progress?.Report(new TrainingProgress("Chunking", 5, $"Splitting {sourceFiles.Length} file(s) into clips"));
        foreach (string source in sourceFiles)
            await ChunkAudio(source, audioDir, ct);

        progress?.Report(new TrainingProgress("Transcribing", 15, "Transcribing audio with Whisper"));
        string trainList = await Transcribe(audioDir, workDir, ct);

        progress?.Report(new TrainingProgress("Preparing", 25, "Downloading pretrained model if needed"));
        string baseModel = await EnsurePretrainedModel(ct);

        // Resume from existing checkpoint if one exists for this model name
        string existingModel = Path.Combine(outputDir, "model.pth");
        string pretrainedModel = File.Exists(existingModel) ? existingModel : baseModel;
        if (pretrainedModel == existingModel)
            logger?.LogInformation("[StyleTTS2-Train] Resuming from existing checkpoint: {Path}", existingModel);

        string configPath = WriteTrainingConfig(workDir, trainList, audioDir, outputDir, pretrainedModel);

        progress?.Report(new TrainingProgress("Training", 0, $"Fine-tuning StyleTTS2 for {epochs} epochs"));
        await FineTune(configPath, progress, ct);

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
        string python     = Path.Combine(styleTtsPath, VenvPython);
        string scriptPath = Path.Combine(Path.GetTempPath(), "ari_chunk.py");
        await File.WriteAllTextAsync(scriptPath, BuildChunkScript(source, audioDir, CHUNK_SECS), ct);
        await RunPython(python, scriptPath, null, ct);
    }

    private async Task<string> Transcribe(string audioDir, string workDir, CancellationToken ct)
    {
        string whisper    = Path.Combine(styleTtsPath, VenvWhisper);
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
        string modelsDir = Path.Combine(styleTtsPath, "Models", "LibriTTS");
        string modelPath = Path.Combine(modelsDir, "epochs_2nd_00020.pth");

        if (File.Exists(modelPath)) return modelPath;

        Directory.CreateDirectory(modelsDir);
        logger?.LogInformation("[StyleTTS2-Train] Downloading pretrained LibriTTS model...");

        string python     = Path.Combine(styleTtsPath, VenvPython);
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
        string workDir, string trainList, string audioDir, string outputDir, string pretrainedModel)
    {
        string configPath = Path.Combine(workDir, "config_ft.yml");

        string yaml = $"""
log_dir: "{outputDir}"
save_freq: {saveEveryNEpochs}
log_interval: 10
device: "{DetectDevice()}"
epochs: {epochs}
batch_size: 2
max_len: 400
pretrained_model: "{pretrainedModel}"
second_stage_load_pretrained: true
load_only_params: true

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
  diff_epoch: 10
  joint_epoch: 30

optimizer_params:
  lr: 0.0001
  bert_lr: 0.00001
  ft_lr: 0.0001

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
        IProgress<TrainingProgress>? progress,
        CancellationToken ct)
    {
        string python      = Path.Combine(styleTtsPath, VenvPython);
        string trainScript = Path.Combine(Path.GetTempPath(), "ari_train_stt2.py");
        await File.WriteAllTextAsync(trainScript,
            "import torch, sys\n" +
            $"sys.path.insert(0, r'{styleTtsPath}')\n" +
            // PyTorch 2.6 changed torch.load default to weights_only=True — patch it back for StyleTTS2
            "_orig = torch.load\n" +
            "torch.load = lambda *a, **kw: _orig(*a, **{**kw, 'weights_only': False})\n" +
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

        _ = Task.Run(async () =>
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
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                stderrLines.AppendLine(line);
                logger?.LogInformation("[StyleTTS2-Train] {Line}", line);
            }
        }, CancellationToken.None);

        await process.WaitForExitAsync(CancellationToken.None);

        if (process.ExitCode != 0 && !ct.IsCancellationRequested)
            throw new Exception($"StyleTTS2 training failed:\n{stderrLines}");
    }

    private static string OrganiseCheckpoints(string outputDir)
    {
        // Move each epoch_2nd_NNNNN.pth → Checkpoints/<epoch>_epochs/epoch_2nd_NNNNN.pth
        string checkpointsDir = Path.Combine(outputDir, "Checkpoints");
        Directory.CreateDirectory(checkpointsDir);

        string[] rawCheckpoints = Directory.GetFiles(outputDir, "epoch_2nd_*.pth");
        if (rawCheckpoints.Length == 0)
            rawCheckpoints = Directory.GetFiles(outputDir, "epoch_*.pth");

        foreach (string pth in rawCheckpoints)
        {
            string fname = Path.GetFileNameWithoutExtension(pth);
            // epoch_2nd_00004 → index 4 → human epoch 5
            string numPart = fname.Split('_').Last();
            int humanEpoch = int.TryParse(numPart, out int idx) ? idx + 1 : 0;
            string epochDir = Path.Combine(checkpointsDir, $"{humanEpoch}_epochs");
            Directory.CreateDirectory(epochDir);
            File.Move(pth, Path.Combine(epochDir, Path.GetFileName(pth)), overwrite: true);
        }

        // Best = highest epoch in Checkpoints
        string[] all = Directory.GetFiles(checkpointsDir, "*.pth", SearchOption.AllDirectories);
        if (all.Length == 0)
            throw new FileNotFoundException($"No checkpoint found in {outputDir}");

        string best = all.OrderByDescending(File.GetLastWriteTime).First();
        string modelDest = Path.Combine(outputDir, "model.pth");
        File.Copy(best, modelDest, overwrite: true);

        // Promote config so the voice module finds config.yml
        string configFt = Path.Combine(outputDir, "config_ft.yml");
        if (File.Exists(configFt))
            File.Copy(configFt, Path.Combine(outputDir, "config.yml"), overwrite: true);

        return modelDest;
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
            Arguments              = $"\"{wavFile}\" --model base.en --output_format txt --output_dir \"{outDir}\" --fp16 False",
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

    private static readonly (string Wrong, string Right)[] NameFixes =
    [
        ("Shubhi", "Voice"), ("Shubey", "Voice"), ("Shuby", "Voice"),
        ("Shubie", "Voice"), ("Shubi", "Voice"), ("Shuwi", "Voice"),
    ];

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
        // Checked at trainer construction time on the host machine
        if (OperatingSystem.IsMacOS()) return "mps";
        return "cuda";
    }

    private static string BuildChunkScript(string source, string outDir, int chunkSecs)
    {
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
            "    if len(chunk) < sr * 3: continue\n" +
            $"    out = os.path.join(r'{outDir}', f'chunk_{{i:04d}}.wav')\n" +
            "    sf.write(out, chunk, sr)\n";
    }
}

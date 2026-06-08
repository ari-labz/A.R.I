using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

public record TrainingProgress(string Step, int Percent, string? Detail = null);

public class F5Trainer(
    string   f5Path,
    string   voicesPath,
    string   audioPath,
    string   modelName,
    int      epochs          = 100,
    int      saveEveryNEpochs = 10,
    ILogger? logger          = null)
{
    private const string VENV_PYTHON  = "venv/bin/python";
    private const string VENV_WHISPER = "venv/bin/whisper";
    private const int    CHUNK_SECS   = 15;

    public async Task<string> Train(IProgress<TrainingProgress>? progress = null, CancellationToken ct = default)
    {
        string workDir   = Path.Combine(f5Path, "training", modelName);
        string audioDir  = Path.Combine(workDir, "wavs");
        string metaFile  = Path.Combine(workDir, "metadata.csv");
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
        await Transcribe(audioDir, metaFile, ct);

        progress?.Report(new TrainingProgress("Preparing", 25, "Converting dataset to Arrow format"));
        string datasetPath = await PrepareDataset(metaFile, ct);

        progress?.Report(new TrainingProgress("Training", 30, $"Fine-tuning F5-TTS for {epochs} epochs"));
        await FineTune(outputDir, progress, ct);

        progress?.Report(new TrainingProgress("Saving", 98, "Copying checkpoint to Voices folder"));
        string checkpoint = await CopyCheckpoint(outputDir, ct);
        progress?.Report(new TrainingProgress("Complete", 100, $"Model saved to {checkpoint}"));
        return checkpoint;
    }

    private async Task ChunkAudio(string source, string audioDir, CancellationToken ct)
    {
        string python     = Path.Combine(f5Path, VENV_PYTHON);
        string scriptPath = Path.Combine(Path.GetTempPath(), "ari_chunk.py");
        await File.WriteAllTextAsync(scriptPath, BuildChunkScript(source, audioDir, CHUNK_SECS), ct);
        await RunPython(python, scriptPath, null, ct);
    }

    private async Task Transcribe(string audioDir, string metaFile, CancellationToken ct)
    {
        string whisper  = Path.Combine(f5Path, VENV_WHISPER);
        string[] wavs   = Directory.GetFiles(audioDir, "*.wav");
        StringBuilder csv = new("audio_file|text\n");

        foreach (string wav in wavs)
        {
            string transcript = await RunWhisper(whisper, wav, ct);
            if (!string.IsNullOrWhiteSpace(transcript))
                csv.AppendLine($"{wav}|{transcript.Trim()}");
        }

        await File.WriteAllTextAsync(metaFile, csv.ToString(), ct);
        logger?.LogInformation("[F5-Train] Transcribed {Count} clips", wavs.Length);
    }

    private async Task<string> PrepareDataset(string metaFile, CancellationToken ct)
    {
        string python     = Path.Combine(f5Path, VENV_PYTHON);
        string scriptPath = Path.Combine(Path.GetTempPath(), "ari_prep.py");
        string prepScript = BuildPrepScript(metaFile, modelName);
        await File.WriteAllTextAsync(scriptPath, prepScript, ct);

        string datasetPath = await RunPythonWithOutput(python, scriptPath, ct);
        return datasetPath.Trim();
    }

    private async Task FineTune(
        string outputDir,
        IProgress<TrainingProgress>? progress,
        CancellationToken ct)
    {
        string python     = Path.Combine(f5Path, VENV_PYTHON);
        string scriptPath = Path.Combine(Path.GetTempPath(), "ari_train.py");
        await File.WriteAllTextAsync(scriptPath, BuildTrainScript(modelName, epochs, saveEveryNEpochs), ct);

        ProcessStartInfo info = new()
        {
            FileName               = python,
            Arguments              = $"\"{scriptPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = f5Path,
        };

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start F5-TTS training process.");

        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) != null)
            {
                logger?.LogDebug("[F5-Train] {Line}", line);
                TrainingProgress? update = ParseProgress(line, epochs);
                if (update != null)
                    progress?.Report(update);
            }
        }, ct);

        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new Exception($"F5-TTS training failed: {stderr}");
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

    private async Task<string> RunPythonWithOutput(string python, string scriptPath, CancellationToken ct)
    {
        ProcessStartInfo info = new()
        {
            FileName               = python,
            Arguments              = $"\"{scriptPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start Python.");

        string stdout = await process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new Exception($"Python script failed: {stderr}");

        return stdout;
    }

    private async Task<string> CopyCheckpoint(string outputDir, CancellationToken ct)
    {
        string python     = Path.Combine(f5Path, VENV_PYTHON);
        string scriptPath = Path.Combine(Path.GetTempPath(), "ari_find_ckpt.py");
        await File.WriteAllTextAsync(scriptPath,
            "from importlib.resources import files\n" +
            $"print(str(files('f5_tts').joinpath('../../ckpts/{modelName}')))\n", ct);

        string ckptsDir = (await RunPythonWithOutput(python, scriptPath, ct)).Trim();

        string[] candidates = Directory.Exists(ckptsDir)
            ? Directory.GetFiles(ckptsDir, "*.pt", SearchOption.AllDirectories)
            : Array.Empty<string>();

        if (candidates.Length == 0)
            throw new FileNotFoundException($"No checkpoint found in {ckptsDir} — training may not have completed a save interval");

        string latest = candidates.OrderByDescending(File.GetLastWriteTime).First();
        Directory.CreateDirectory(outputDir);
        string dest = Path.Combine(outputDir, Path.GetFileName(latest));
        File.Copy(latest, dest, overwrite: true);
        logger?.LogInformation("[F5-Train] Checkpoint copied: {Src} → {Dest}", latest, dest);
        return dest;
    }

    private static TrainingProgress? ParseProgress(string line, int totalEpochs)
    {
        if (!line.Contains("epoch") || !line.Contains("/"))
            return null;

        int slash = line.IndexOf('/');
        if (slash < 1) return null;

        int start = slash - 1;
        while (start > 0 && char.IsDigit(line[start - 1])) start--;
        if (!int.TryParse(line[start..slash], out int current)) return null;

        int percent = (int)((double)current / totalEpochs * 70) + 30;
        return new TrainingProgress("Training", Math.Min(percent, 99), $"Epoch {current}/{totalEpochs}");
    }

    private static string BuildChunkScript(string source, string outDir, int chunkSecs)
    {
        return
            "import soundfile as sf, numpy as np, os\n" +
            $"data, sr = sf.read(r'{source}')\n" +
            "if data.ndim > 1:\n" +
            "    data = data.mean(axis=1)\n" +
            $"chunk_samples = sr * {chunkSecs}\n" +
            "for i, start in enumerate(range(0, len(data), chunk_samples)):\n" +
            "    chunk = data[start:start + chunk_samples]\n" +
            "    if len(chunk) < sr * 3: continue\n" +
            $"    out = os.path.join(r'{outDir}', f'chunk_{{i:04d}}.wav')\n" +
            "    sf.write(out, chunk, sr)\n";
    }

    private static string BuildPrepScript(string metaFile, string modelName)
    {
        return
            "from importlib.resources import files\n" +
            "from pathlib import Path\n" +
            "import shutil\n" +
            "vocab_dir = Path(str(files('f5_tts').joinpath('../../data/Emilia_ZH_EN_pinyin')))\n" +
            "vocab_dir.mkdir(parents=True, exist_ok=True)\n" +
            "vocab_path = vocab_dir / 'vocab.txt'\n" +
            "if not vocab_path.exists():\n" +
            "    from cached_path import cached_path\n" +
            "    downloaded = cached_path('hf://SWivid/F5-TTS/F5TTS_v1_Base/vocab.txt')\n" +
            "    shutil.copy(downloaded, vocab_path)\n" +
            "    print(f'Downloaded pretrained vocab to {vocab_path}')\n" +
            "from f5_tts.train.datasets.prepare_csv_wavs import prepare_and_save_set\n" +
            $"dataset_path = str(files('f5_tts').joinpath('../../data/{modelName}_pinyin'))\n" +
            $"prepare_and_save_set(r'{metaFile}', dataset_path, is_finetune=True)\n" +
            "print(dataset_path)\n";
    }

    private static string BuildTrainScript(string modelName, int epochs, int saveEveryNEpochs)
    {
        int warmupUpdates = Math.Min(50, epochs);

        return
            "import sys, math\n" +
            "import multiprocessing\n" +
            "import torch.utils.data\n" +
            "from importlib.resources import files\n" +
            "from f5_tts.model.dataset import load_dataset\n" +
            "\n" +
            "multiprocessing.set_start_method('fork', force=True)\n" +
            "\n" +
            "_orig_init = torch.utils.data.DataLoader.__init__\n" +
            "def _patched_init(self, *args, **kwargs):\n" +
            "    kwargs['num_workers'] = 0\n" +
            "    kwargs['pin_memory'] = False\n" +
            "    kwargs['persistent_workers'] = False\n" +
            "    _orig_init(self, *args, **kwargs)\n" +
            "torch.utils.data.DataLoader.__init__ = _patched_init\n" +
            "\n" +
            "# Estimate updates per epoch to convert epoch-based save interval to update-based\n" +
            $"dataset = load_dataset('{modelName}')\n" +
            $"updates_per_epoch = max(1, math.ceil(len(dataset) / 2))\n" +
            $"save_every = max(1, updates_per_epoch * {saveEveryNEpochs})\n" +
            $"warmup = min({warmupUpdates}, updates_per_epoch)\n" +
            "print(f'Dataset size: {{len(dataset)}} | updates/epoch: {{updates_per_epoch}} | save every: {{save_every}} updates')\n" +
            "\n" +
            "from f5_tts.train.finetune_cli import main\n" +
            "sys.argv = [\n" +
            "    'finetune',\n" +
            "    '--exp_name', 'F5TTS_v1_Base',\n" +
            $"    '--dataset_name', '{modelName}',\n" +
            $"    '--epochs', '{epochs}',\n" +
            "    '--batch_size_per_gpu', '2',\n" +
            "    '--batch_size_type', 'sample',\n" +
            "    '--num_warmup_updates', str(warmup),\n" +
            "    '--save_per_updates', str(save_every),\n" +
            "    '--last_per_updates', str(save_every),\n" +
            "    '--finetune',\n" +
            "]\n" +
            "\n" +
            "if __name__ == '__main__':\n" +
            "    main()\n";
    }
}

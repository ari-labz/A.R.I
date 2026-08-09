using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

public class OrpheusTrainer(
    string   orpheusSourcePath,
    string   voicesPath,
    string   audioPath,
    string   voiceName,
    int      epochs    = 3,
    string   quantType = "q4_k_m",
    Dictionary<string, string>? transcripts = null,
    ILogger? logger    = null) : IVoiceTrainer
{
    public async Task<string> Train(IProgress<TrainingProgress>? progress = null, CancellationToken ct = default)
    {
        string trainingDir = Path.Combine(orpheusSourcePath, "training");
        string outputDir   = Path.Combine(voicesPath, "Orpheus", voiceName);
        string dataDir     = Path.Combine(outputDir, "training_data");

        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(dataDir);

        string python = FindPython();
        string venvDir = Path.Combine(orpheusSourcePath, "training", "venv");

        // Step 0: Set up venv + install deps if needed
        string venvPython = await EnsureVenv(python, venvDir, trainingDir, progress, ct);

        // Step 1: Copy audio files to training data dir
        progress?.Report(new TrainingProgress("Preparing", 5, "Copying audio clips"));
        CopyAudioFiles(audioPath, dataDir);

        // Step 2: Encode audio → SNAC tokens
        string datasetPath = Path.Combine(outputDir, "dataset.jsonl");
        progress?.Report(new TrainingProgress("Encoding", 10, "Encoding audio to SNAC tokens (this takes a while)"));
        await RunPythonScript(venvPython, trainingDir,
            "prepare_dataset.py",
            $"--audio-dir \"{dataDir}\" --voice \"{voiceName}\" --output \"{datasetPath}\"",
            progress, "Encoding", 10, 30, ct);

        // Step 3: LoRA fine-tune
        progress?.Report(new TrainingProgress("Training", 35, $"Fine-tuning Orpheus for {epochs} epochs"));
        string loraDir = Path.Combine(outputDir, "lora");
        await RunPythonScript(venvPython, trainingDir,
            "train.py",
            $"--dataset \"{datasetPath}\" --output \"{loraDir}\" --epochs {epochs}",
            progress, "Training", 35, 85, ct);

        // Step 4: Export to GGUF
        string ggufPath = Path.Combine(outputDir, $"{voiceName}.gguf");
        progress?.Report(new TrainingProgress("Exporting", 88, $"Merging LoRA and converting to GGUF ({quantType})"));
        await RunPythonScript(venvPython, trainingDir,
            "export_gguf.py",
            $"--lora \"{loraDir}\" --output \"{ggufPath}\" --quant {quantType}",
            progress, "Exporting", 88, 98, ct);

        progress?.Report(new TrainingProgress("Complete", 100, $"Model saved to {ggufPath}"));
        return ggufPath;
    }

    private void CopyAudioFiles(string source, string dest)
    {
        if (!Directory.Exists(source))
        {
            logger?.LogWarning("[OrpheusTrainer] Audio source directory does not exist: {Path}", source);
            return;
        }

        var sourceWavs = Directory.GetFiles(source, "*.wav");
        if (sourceWavs.Length == 0)
            logger?.LogWarning("[OrpheusTrainer] No .wav files found in {Path}", source);

        if (transcripts is not null)
        {
            var transcriptWavNames = new HashSet<string>(
                transcripts.Keys.Select(k => Path.ChangeExtension(k, ".wav")),
                StringComparer.OrdinalIgnoreCase);

            int copied = 0;
            foreach (string file in sourceWavs)
            {
                string name = Path.GetFileName(file);
                if (transcriptWavNames.Contains(name))
                {
                    File.Copy(file, Path.Combine(dest, name), overwrite: true);
                    copied++;
                }
            }

            foreach (var (fileName, text) in transcripts)
            {
                string wavName = Path.ChangeExtension(fileName, ".wav");
                string wavDest = Path.Combine(dest, wavName);
                if (!File.Exists(wavDest))
                    logger?.LogWarning("[OrpheusTrainer] Transcript for '{File}' has no matching wav in source", fileName);

                string txtName = Path.ChangeExtension(fileName, ".txt");
                File.WriteAllText(Path.Combine(dest, txtName), text);
            }

            logger?.LogInformation("[OrpheusTrainer] Copied {Copied}/{Total} wav files ({Transcripts} transcripts)",
                copied, sourceWavs.Length, transcripts.Count);

            if (copied == 0)
                throw new InvalidOperationException(
                    $"No wav files matched any transcript keys. Source has {sourceWavs.Length} wavs " +
                    $"(e.g. {(sourceWavs.Length > 0 ? Path.GetFileName(sourceWavs[0]) : "none")}), " +
                    $"transcripts expect (e.g. {transcripts.Keys.FirstOrDefault() ?? "none"}).");
        }
        else
        {
            foreach (string file in sourceWavs)
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            foreach (string file in Directory.GetFiles(source, "*.txt"))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
    }

    private async Task<string> EnsureVenv(string python, string venvDir, string trainingDir,
        IProgress<TrainingProgress>? progress, CancellationToken ct)
    {
        string venvPython = Path.Combine(venvDir, "bin", "python3");
        if (OperatingSystem.IsWindows())
            venvPython = Path.Combine(venvDir, "Scripts", "python.exe");

        if (File.Exists(venvPython))
        {
            try
            {
                var check = Process.Start(new ProcessStartInfo
                {
                    FileName = venvPython, Arguments = "--version",
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                });
                check?.WaitForExit(5000);
                if (check?.ExitCode == 0) return venvPython;
            }
            catch { }
            logger?.LogWarning("[OrpheusTrainer] Existing venv Python is broken, recreating");
            Directory.Delete(venvDir, recursive: true);
        }

        progress?.Report(new TrainingProgress("Setup", 0, "Creating Python virtual environment"));

        var venvInfo = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"-m venv \"{venvDir}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using (var proc = Process.Start(venvInfo) ?? throw new InvalidOperationException("Failed to create venv"))
        {
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new Exception($"venv creation failed: {await proc.StandardError.ReadToEndAsync(ct)}");
        }

        progress?.Report(new TrainingProgress("Setup", 2, "Installing training dependencies (unsloth, snac, torch…)"));

        string requirementsPath = Path.Combine(trainingDir, "requirements.txt");
        var pipInfo = new ProcessStartInfo
        {
            FileName = venvPython,
            Arguments = $"-m pip install -r \"{requirementsPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = trainingDir,
        };
        using (var proc = Process.Start(pipInfo) ?? throw new InvalidOperationException("Failed to run pip"))
        {
            var stderr = new StringBuilder();
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync(CancellationToken.None)) != null)
                    logger?.LogInformation("[Orpheus-Setup] {Line}", line);
            }, CancellationToken.None);
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync(CancellationToken.None)) != null)
                {
                    stderr.AppendLine(line);
                    logger?.LogWarning("[Orpheus-Setup] {Line}", line);
                }
            }, CancellationToken.None);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new Exception($"pip install failed:\n{stderr}");
        }

        return venvPython;
    }

    private async Task RunPythonScript(string python, string workDir, string script, string args,
        IProgress<TrainingProgress>? progress, string stepName, int startPct, int endPct, CancellationToken ct)
    {
        string scriptPath = Path.Combine(workDir, script);
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"Training script not found: {scriptPath}");

        var info = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"\"{scriptPath}\" {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workDir,
        };

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Failed to start {script}");

        using var killReg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        var stderrLines = new StringBuilder();
        int lineCount = 0;

        Task stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(CancellationToken.None)) != null)
            {
                logger?.LogInformation("[Orpheus-{Step}] {Line}", stepName, line);
                lineCount++;

                // Parse train.py epoch progress
                if (line.StartsWith("[train]") && line.Contains("epoch", StringComparison.OrdinalIgnoreCase))
                {
                    var epochProgress = ParseEpochProgress(line, epochs);
                    if (epochProgress is not null)
                    {
                        int pct = startPct + (int)((endPct - startPct) * (epochProgress.Value / 100.0));
                        progress?.Report(new TrainingProgress(stepName, pct, $"Epoch {line.Split(' ').LastOrDefault()}"));
                    }
                }
                else if (line.Contains("OK ") || line.Contains("Done"))
                {
                    progress?.Report(new TrainingProgress(stepName,
                        Math.Min(startPct + (endPct - startPct) / 2, endPct), line));
                }
            }
        }, CancellationToken.None);

        Task stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None)) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    stderrLines.AppendLine(line);
                    logger?.LogWarning("[Orpheus-{Step}] [stderr] {Line}", stepName, line);

                    var pctMatch = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)%\|");
                    if (pctMatch.Success && int.TryParse(pctMatch.Groups[1].Value, out int stderrPct))
                    {
                        int pct = startPct + (int)((endPct - startPct) * (stderrPct / 100.0));
                        progress?.Report(new TrainingProgress(stepName, pct, line.Trim()));
                    }
                }
            }
        }, CancellationToken.None);

        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0 && !ct.IsCancellationRequested)
            throw new Exception($"Orpheus {script} failed:\n{stderrLines}");
    }

    private static int? ParseEpochProgress(string line, int totalEpochs)
    {
        // Look for patterns like "Epoch 2/3" or similar in training output
        var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)/(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int current))
        {
            int total = int.TryParse(match.Groups[2].Value, out int t) ? t : totalEpochs;
            return (int)(current * 100.0 / total);
        }
        return null;
    }

    private const int MinPythonMajor = 3;
    private const int MinPythonMinor = 10;

    private static string FindPython()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "python3", "python" }
            : new[] { "python3.14", "python3.13", "python3.12", "python3.11", "python3.10", "python3", "python" };

        foreach (string name in candidates)
        {
            string? path = WhichBinary(name);
            if (path is null) continue;

            var (major, minor) = GetPythonVersion(path);
            if (major >= MinPythonMajor && minor >= MinPythonMinor)
                return path;
        }
        throw new FileNotFoundException(
            $"Python {MinPythonMajor}.{MinPythonMinor}+ not found. Install it to train Orpheus voices.");
    }

    private static string? WhichBinary(string name)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = name,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(info);
            string? path = p?.StandardOutput.ReadLine()?.Trim();
            p?.WaitForExit();
            return p?.ExitCode == 0 && !string.IsNullOrEmpty(path) ? path : null;
        }
        catch { return null; }
    }

    private static (int major, int minor) GetPythonVersion(string pythonPath)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = "-c \"import sys; print(sys.version_info.major, sys.version_info.minor)\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            string? output = p?.StandardOutput.ReadLine()?.Trim();
            p?.WaitForExit();
            if (output is not null)
            {
                var parts = output.Split(' ');
                if (parts.Length == 2 && int.TryParse(parts[0], out int maj) && int.TryParse(parts[1], out int min))
                    return (maj, min);
            }
        }
        catch { }
        return (0, 0);
    }
}

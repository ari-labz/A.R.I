using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

public record TranscribedClip(string FileName, string Transcript, float Duration);

public class AudioTranscriber
{
    private const int CHUNK_SECS     = 8;
    private const int MIN_CHUNK_SECS = 2;

    private readonly string  stageDir;
    private readonly string  workDir;
    private readonly ILogger logger;

    public string Step    { get; private set; } = "Starting";
    public int    Percent { get; private set; }
    public bool   IsRunning { get; private set; } = true;
    public bool   IsSuccess { get; private set; }
    public string? Error    { get; private set; }
    public IReadOnlyList<TranscribedClip> Clips => _clips.AsReadOnly();

    private readonly List<TranscribedClip> _clips = new();

    private static readonly object gate = new();
    private static AudioTranscriber? _current;
    public static AudioTranscriber? Current { get { lock (gate) return _current; } }

    private AudioTranscriber(string stageDir, string workDir, ILogger logger)
    {
        this.stageDir = stageDir;
        this.workDir  = workDir;
        this.logger   = logger;
    }

    public static AudioTranscriber Start(string stageDir, string dataDir, ILogger logger, CancellationToken ct)
    {
        lock (gate)
        {
            if (_current?.IsRunning == true)
                throw new InvalidOperationException("A transcription job is already running.");
            string workDir = Path.Combine(dataDir, "transcribe_work");
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
            Directory.CreateDirectory(workDir);
            var t = new AudioTranscriber(stageDir, workDir, logger);
            _current = t;
            _ = Task.Run(() => t.Run(ct), ct);
            return t;
        }
    }

    private async Task Run(CancellationToken ct)
    {
        try
        {
            string python  = Paths.StyleTts2Python;
            string whisper = Paths.StyleTts2Whisper;

            if (!File.Exists(python))
                throw new FileNotFoundException("StyleTTS2 Python not found — VoiceSynthesis module must be set up first.");

            string wavDir = Path.Combine(workDir, "wavs");
            Directory.CreateDirectory(wavDir);

            // Step 1: chunk uploaded audio into clips
            Step = "Chunking"; Percent = 5;
            string[] sourceFiles = Directory.GetFiles(stageDir)
                .Where(f => new[] { ".wav", ".mp3", ".flac", ".ogg" }
                    .Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToArray();

            if (sourceFiles.Length == 0)
                throw new FileNotFoundException("No audio files found in staging directory.");

            foreach (string source in sourceFiles)
            {
                float dur = await GetDuration(python, source, ct);
                if (dur <= CHUNK_SECS)
                {
                    string dest = Path.Combine(wavDir, Path.GetFileName(source));
                    File.Copy(source, dest, overwrite: true);
                }
                else
                {
                    await ChunkAudio(python, source, wavDir, ct);
                }
            }

            string[] chunks = Directory.GetFiles(wavDir, "*.wav");
            logger.LogInformation("[Transcriber] Chunked into {Count} clips", chunks.Length);

            // Step 2: transcribe each clip with Whisper
            Step = "Transcribing"; Percent = 20;
            for (int i = 0; i < chunks.Length; i++)
            {
                string wav = chunks[i];
                string transcript = await RunWhisper(whisper, wav, ct);
                float duration = await GetDuration(python, wav, ct);

                _clips.Add(new TranscribedClip(
                    Path.GetFileName(wav),
                    transcript.Trim(),
                    duration));

                Percent = 20 + (int)(75.0 * (i + 1) / chunks.Length);
            }

            Step = "Complete"; Percent = 100;
            IsSuccess = true;
            logger.LogInformation("[Transcriber] Transcribed {Count} clips", _clips.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
            Step  = "Error";
            logger.LogError(ex, "[Transcriber] Failed");
        }
        finally { IsRunning = false; }
    }

    public string WorkDir => workDir;

    private async Task ChunkAudio(string python, string source, string outDir, CancellationToken ct)
    {
        string stem = new string(Path.GetFileNameWithoutExtension(source)
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

        string script =
            "import soundfile as sf, numpy as np, os, torch, torchaudio\n" +
            $"data, sr = sf.read(r'{source}')\n" +
            "if data.ndim > 1:\n    data = data.mean(axis=1)\n" +
            "if sr != 24000:\n" +
            "    t = torch.tensor(data).unsqueeze(0).float()\n" +
            "    t = torchaudio.functional.resample(t, sr, 24000)\n" +
            "    data = t.squeeze(0).numpy(); sr = 24000\n" +
            $"chunk_samples = sr * {CHUNK_SECS}\n" +
            "for i, start in enumerate(range(0, len(data), chunk_samples)):\n" +
            "    chunk = data[start:start + chunk_samples]\n" +
            $"    if len(chunk) < sr * {MIN_CHUNK_SECS}: continue\n" +
            $"    out = os.path.join(r'{outDir}', f'chunk_{stem}_{{i:04d}}.wav')\n" +
            "    sf.write(out, chunk, sr)\n";

        string scriptPath = Path.Combine(workDir, "chunk.py");
        await File.WriteAllTextAsync(scriptPath, script, ct);
        await RunProcess(python, $"\"{scriptPath}\"", null, ct);
    }

    private async Task<string> RunWhisper(string whisper, string wavFile, CancellationToken ct)
    {
        string outDir = Path.GetDirectoryName(wavFile)!;
        await RunProcess(whisper,
            $"\"{wavFile}\" --model base.en --output_format txt --output_dir \"{outDir}\" --fp16 False --condition_on_previous_text False",
            null, ct);

        string txtFile = Path.ChangeExtension(wavFile, ".txt");
        return File.Exists(txtFile) ? await File.ReadAllTextAsync(txtFile, ct) : "";
    }

    private async Task<float> GetDuration(string python, string wavFile, CancellationToken ct)
    {
        string script = $"import soundfile as sf; d,sr = sf.read(r'{wavFile}'); print(len(d)/sr)";
        string scriptPath = Path.Combine(workDir, "dur.py");
        await File.WriteAllTextAsync(scriptPath, script, ct);

        var info = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"\"{scriptPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(info) ?? throw new InvalidOperationException("Failed to start Python");
        string output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return float.TryParse(output.Trim(), out float dur) ? dur : 0f;
    }

    private async Task RunProcess(string exe, string args, string? workDir, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (workDir != null) info.WorkingDirectory = workDir;

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Failed to start {exe}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new Exception($"Process failed: {await stderrTask}");
    }
}

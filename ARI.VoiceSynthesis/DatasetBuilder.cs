using System.Diagnostics;
using ARI.Common;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

/// <summary>Runs demucs + Whisper over a folder of uploaded clips, splits each clip into
/// parts, and packages the chosen original/processed variants into a downloadable dataset.
/// One build runs at a time; the active build is reached through <see cref="Current"/>.</summary>
public class DatasetBuilder
{
    private const string PROCESS_SCRIPT = "dataset_process.py";
    private const int    PERCENT_SCALE  = 100;

    private static readonly JsonSerializerOptions manifestFormat = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object  gate = new();
    private static DatasetBuilder?  current;

    private readonly string                  styleTtsPath;
    private readonly string                  dataDir;
    private readonly string                  stageDir;
    private readonly ILogger                 logger;
    private readonly CancellationTokenSource cancellation;

    public string  Step      { get; private set; } = "Starting";
    public int     Percent   { get; private set; }
    public bool    IsRunning { get; private set; } = true;
    public bool    IsSuccess { get; private set; }
    public string? Error     { get; private set; }

    // Read live from disk: the script republishes the manifest after every clip.
    public IReadOnlyList<DatasetPart> Parts => ReadManifest();

    public static DatasetBuilder? Current
    {
        get { lock (gate) return current; }
    }

    // styleTtsPath is install content (StyleTTS2 source — dataset_process.py); dataDir is
    // AppDataRoot-based mutable state (the venv StyleTtsSetupService provisions).
    private DatasetBuilder(string styleTtsPath, string dataDir, string stageDir, ILogger logger, CancellationToken appStopping)
    {
        this.styleTtsPath = styleTtsPath;
        this.dataDir      = dataDir;
        this.stageDir     = stageDir;
        this.logger       = logger;
        cancellation      = CancellationTokenSource.CreateLinkedTokenSource(appStopping);
    }

    public static DatasetBuilder Start(string styleTtsPath, string dataDir, string stageDir, ILogger logger, CancellationToken appStopping)
    {
        lock (gate)
        {
            if (current?.IsRunning == true)
                throw new InvalidOperationException("A dataset build is already running.");
            DatasetBuilder builder = new(styleTtsPath, dataDir, stageDir, logger, appStopping);
            current = builder;
            builder.Run();
            return builder;
        }
    }

    private void Run()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RunScript();
                IsSuccess = true;
                Step      = "Complete";
                Percent   = PERCENT_SCALE;
            }
            catch (OperationCanceledException)
            {
                Error = "Build cancelled.";
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                logger.LogError(ex, "[Dataset] Build failed");
            }
            finally { IsRunning = false; }
        }, cancellation.Token);
    }

    private async Task RunScript()
    {
        string python = Paths.StyleTts2Python;
        string script = Path.Combine(styleTtsPath, PROCESS_SCRIPT);

        ProcessStartInfo info = new()
        {
            FileName               = python,
            Arguments              = $"\"{script}\" \"{stageDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = styleTtsPath,
        };

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start dataset processing.");

        // Kill the whole tree on shutdown so demucs/torch don't outlive ARI as orphans.
        using CancellationTokenRegistration kill = cancellation.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        Task stdout = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(CancellationToken.None)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                logger.LogInformation("[Dataset] {Line}", line);
                ReadProgress(line);
            }
        }, CancellationToken.None);

        StringBuilder errors = new();
        Task stderr = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                errors.AppendLine(line);
                logger.LogWarning("[Dataset] [stderr] {Line}", line);
            }
        }, CancellationToken.None);

        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdout, stderr);

        if (process.ExitCode != 0 && !cancellation.IsCancellationRequested)
            throw new Exception($"Dataset processing failed:\n{errors}");
    }

    private void ReadProgress(string line)
    {
        if (!line.StartsWith("PROGRESS ")) return;
        string[] segments = line["PROGRESS ".Length..].Split(' ', 2);
        string[] fraction = segments[0].Split('/');
        if (fraction.Length == 2 &&
            int.TryParse(fraction[0], out int done) &&
            int.TryParse(fraction[1], out int total) && total > 0)
        {
            Percent = done * PERCENT_SCALE / total;
            Step    = segments.Length > 1 ? segments[1] : "Processing";
        }
    }

    private List<DatasetPart> ReadManifest()
    {
        string manifestPath = Path.Combine(stageDir, "manifest.json");
        if (!File.Exists(manifestPath)) return new();
        return JsonSerializer.Deserialize<List<DatasetPart>>(File.ReadAllText(manifestPath), manifestFormat) ?? new();
    }

    /// <summary>Absolute path of a part's audio for the chosen variant, or null if missing.</summary>
    public string? VariantPath(string name, string variant)
    {
        string folder = variant == "processed" ? "processed" : "original";
        string path   = Path.Combine(stageDir, folder, $"{Path.GetFileName(name)}.wav");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Zips the selected variants under wavs/ alongside a transcript metadata.csv.</summary>
    public byte[] BuildZip(IEnumerable<DatasetSelection> selections)
    {
        Dictionary<string, DatasetPart> transcripts = ReadManifest().ToDictionary(part => part.Name);
        using MemoryStream memory = new();
        using (ZipArchive zip = new(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            StringBuilder metadata = new();
            foreach (DatasetSelection selection in selections)
            {
                string? source = VariantPath(selection.Name, selection.Variant);
                if (source is null) continue;
                zip.CreateEntryFromFile(source, $"wavs/{selection.Name}.wav");
                string transcript = transcripts.TryGetValue(selection.Name, out DatasetPart? part) ? part.Transcript : "";
                metadata.AppendLine($"{selection.Name}.wav|{transcript}");
            }
            using StreamWriter writer = new(zip.CreateEntry("metadata.csv").Open());
            writer.Write(metadata.ToString());
        }
        return memory.ToArray();
    }
}

public record DatasetPart(
    string Clip, string Name, int Part, double Duration, string Language,
    string Transcript, double NoSpeech, double BgRatio, string[] Flags);

public record DatasetSelection(string Name, string Variant);

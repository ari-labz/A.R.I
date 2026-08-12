using System.Diagnostics;
using System.Text;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

/// <summary>
/// Provisions the Python virtual environment shared by the dataset builder (demucs + faster-whisper)
/// and the audio transcriber (faster-whisper + soundfile). Independent of any voice module's venv.
/// </summary>
public class VoiceSynthesisSetupService(ILogger? logger = null)
{
    private const string VENV_SUBDIR = "voicesynth";
    private const string MARKER_FILE = ".deps-installed";
    private const int    DEPS_VERSION = 1;

    private static string Python => OperatingSystem.IsWindows() ? "python" : "/opt/homebrew/bin/python3.11";

    private static readonly string[] Packages =
    [
        "faster-whisper",
        "demucs",
        "soundfile",
        "numpy",
    ];

    public async Task Install()
    {
        string venv = Paths.VoiceSynthesisVenv;
        Directory.CreateDirectory(Path.GetDirectoryName(venv)!);

        string venvPy = Path.Combine(venv, OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python");
        string pip    = Path.Combine(venv, OperatingSystem.IsWindows() ? @"Scripts\pip.exe"    : "bin/pip");
        string marker = Path.Combine(venv, MARKER_FILE);
        string stamp  = $"v{DEPS_VERSION}|{string.Join(",", Packages)}";

        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == stamp)
        {
            logger?.LogInformation("VoiceSynthesis environment already installed.");
            return;
        }

        if (!Directory.Exists(venv))
        {
            logger?.LogInformation("Creating VoiceSynthesis virtual environment...");
            await RunExe(Python, $"-m venv \"{venv}\"");
        }

        logger?.LogInformation("Installing VoiceSynthesis dependencies...");
        await RunExe(venvPy, "-m pip install -q --upgrade pip");
        await RunExe(pip, $"install -q {string.Join(" ", Packages)}");

        File.WriteAllText(marker, stamp);
        logger?.LogInformation("VoiceSynthesis environment ready.");
    }

    private async Task RunExe(string exe, string args)
    {
        ProcessStartInfo info = new()
        {
            FileName               = exe,
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException($"Failed to start: {exe}");

        StringBuilder captured = new();

        Task stdoutTask = StreamLines(process.StandardOutput, line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            captured.AppendLine(line);
            logger?.LogDebug("[setup] {Line}", line);
        });

        Task stderrTask = StreamLines(process.StandardError, line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            captured.AppendLine(line);
            if (line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
                logger?.LogDebug("[setup] {Line}", line);
            else
                logger?.LogWarning("[setup] {Line}", line);
        });

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new SetupException($"VoiceSynthesis setup failed (exit {process.ExitCode}): {exe} {args}",
                                     SetupDiagnostics.Diagnose(captured.ToString()));
    }

    private static async Task StreamLines(StreamReader reader, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            onLine(line);
    }
}

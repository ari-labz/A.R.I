using System.Diagnostics;
using System.Text;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

// styleTtsPath is install content (the StyleTTS2 source) — read from, never written to.
// dataDir is AppDataRoot-based mutable state (the venv this class provisions).
public class StyleTtsSetupService(string styleTtsPath, string dataDir, ILogger? logger = null)
{
    private const string VENV_SUBDIR = "venv";
    private static string Python     => OperatingSystem.IsWindows() ? "python" : "/opt/homebrew/bin/python3.11";
    private static string PythonArgs => "";

    // StyleTTS2 is now vendored as a git submodule (Xywren/StyleTTS2 fork), so the source files
    // are always present — no clone or source-patching on first run. This only provisions the
    // Python virtual environment (never committed, must be created locally) and its dependencies.
    public async Task Install()
    {
        string venv = Path.Combine(dataDir, VENV_SUBDIR);
        Directory.CreateDirectory(dataDir);

        if (!Directory.Exists(venv))
        {
            logger?.LogInformation("Creating StyleTTS2 virtual environment...");
            await RunExe(Python, $"{PythonArgs}-m venv \"{venv}\"", dataDir);
        }

        string pip    = Path.Combine(venv, OperatingSystem.IsWindows() ? @"Scripts\pip.exe"    : "bin/pip");
        string venvPy = Path.Combine(venv, OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python");
        logger?.LogInformation("Installing StyleTTS2 dependencies...");
        await RunExe(venvPy, "-m pip install -q --upgrade pip", dataDir);

        // Torch build by platform: macOS uses the default PyPI wheel (MPS). On Windows/Linux use the
        // CUDA build only when an NVIDIA GPU is actually present, else CPU — otherwise non-NVIDIA
        // machines fail to resolve torch from the CUDA index ("No matching distribution").
        string torchInstall = OperatingSystem.IsMacOS()
            ? "install -q torch torchaudio"
            : HasNvidiaGpu()
                ? "install -q torch torchaudio --index-url https://download.pytorch.org/whl/cu124"
                : "install -q torch torchaudio --index-url https://download.pytorch.org/whl/cpu";
        await RunExe(pip, torchInstall, dataDir);

        await RunExe(pip, $"install -q --prefer-binary -r \"{Path.Combine(styleTtsPath, "requirements.txt")}\"", styleTtsPath);
        // phonemizer: serve.py's phonemize.py imports phonemizer.backend.EspeakBackend directly.
        // It shells out to libespeak-ng, so the system binary (brew: espeak-ng) has to exist too —
        // not something pip can provide.
        await RunExe(pip, $"install -q openai-whisper flask sounddevice soundfile cached-path tensorboard pandas ipython gruut phonemizer", dataDir);
        await EnsureEspeakNg();

        logger?.LogInformation("StyleTTS2 environment ready.");
    }

    // True when an NVIDIA GPU is available (nvidia-smi runs and exits 0).
    private static bool HasNvidiaGpu()
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo("nvidia-smi")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            });
            if (p is null) return false;
            p.WaitForExit(4000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private async Task EnsureEspeakNg()
    {
        if (OperatingSystem.IsWindows()) return; // phonemize.py only searches macOS/Linux dylib paths today.
        if (await CommandExistsAsync("espeak-ng")) return;

        logger?.LogInformation("espeak-ng not found. Installing via Homebrew...");
        await RunExe("brew", "install espeak-ng", dataDir);

        if (!await CommandExistsAsync("espeak-ng"))
            throw new Exception("Failed to install espeak-ng. Please run: brew install espeak-ng");
    }

    private static async Task<bool> CommandExistsAsync(string cmd)
    {
        try
        {
            using Process p = Process.Start(new ProcessStartInfo("which", cmd)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            })!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private async Task RunExe(string exe, string args, string workDir)
    {
        ProcessStartInfo info = new()
        {
            FileName               = exe,
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = workDir,
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
            throw new SetupException($"Setup step failed (exit {process.ExitCode}): {exe} {args}", SetupDiagnostics.Diagnose(captured.ToString()));
    }

    private static async Task StreamLines(StreamReader reader, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            onLine(line);
    }
}

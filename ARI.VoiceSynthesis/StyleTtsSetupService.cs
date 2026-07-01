using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

public class StyleTtsSetupService(string styleTtsPath, ILogger? logger = null)
{
    private const string VENV_SUBDIR = "venv";
    private static string Python     => OperatingSystem.IsWindows() ? "python" : "/opt/homebrew/bin/python3.11";
    private static string PythonArgs => "";

    // StyleTTS2 is now vendored as a git submodule (Xywren/StyleTTS2 fork), so the source files
    // are always present — no clone or source-patching on first run. This only provisions the
    // Python virtual environment (gitignored, so it must be created locally) and its dependencies.
    public async Task Install()
    {
        string venv    = Path.Combine(styleTtsPath, VENV_SUBDIR);
        string repoDir = styleTtsPath;

        if (!Directory.Exists(venv))
        {
            logger?.LogInformation("Creating StyleTTS2 virtual environment...");
            await RunExe(Python, $"{PythonArgs}-m venv \"{venv}\"", repoDir);
        }

        string pip    = Path.Combine(venv, OperatingSystem.IsWindows() ? @"Scripts\pip.exe"    : "bin/pip");
        string venvPy = Path.Combine(venv, OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python");
        logger?.LogInformation("Installing StyleTTS2 dependencies...");
        await RunExe(venvPy, "-m pip install -q --upgrade pip", repoDir);

        // Platform-specific torch: macOS gets default PyPI build (includes MPS),
        // Windows gets CUDA 12.1 build, Linux falls back to CPU.
        string torchInstall = OperatingSystem.IsMacOS()
            ? "install -q torch torchaudio"
            : OperatingSystem.IsWindows()
                ? "install -q torch torchaudio --index-url https://download.pytorch.org/whl/cu124"
                : "install -q torch torchaudio --index-url https://download.pytorch.org/whl/cpu";
        await RunExe(pip, torchInstall, repoDir);

        await RunExe(pip, $"install -q --prefer-binary -r \"{Path.Combine(repoDir, "requirements.txt")}\"", repoDir);
        await RunExe(pip, $"install -q openai-whisper flask sounddevice soundfile cached-path tensorboard pandas ipython gruut", repoDir);

        logger?.LogInformation("StyleTTS2 environment ready.");
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

        Task stdoutTask = StreamLines(process.StandardOutput, line =>
        {
            if (!string.IsNullOrWhiteSpace(line))
                logger?.LogDebug("[setup] {Line}", line);
        });

        Task stderrTask = StreamLines(process.StandardError, line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
                logger?.LogDebug("[setup] {Line}", line);
            else
                logger?.LogWarning("[setup] {Line}", line);
        });

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new Exception($"Setup step failed (exit {process.ExitCode}): {exe} {args}");
    }

    private static async Task StreamLines(StreamReader reader, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            onLine(line);
    }
}

using System.Diagnostics;
using System.Text;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.Listener;

/// <summary>
/// Provisions the Python virtual environment the Whisper worker (whisper_serve.py) runs in —
/// faster-whisper, webrtcvad, websockets, numpy. Mirrors StyleTtsSetupService's shape, but this
/// venv is entirely independent: no dependency overlap with StyleTTS2's torch/transformers stack,
/// so keeping them separate avoids coupling two unrelated subsystems together.
/// </summary>
public class ListenerSetupService(ILogger? logger = null)
{
    private const string VENV_SUBDIR = "venv";
    private static string Python => OperatingSystem.IsWindows() ? "python" : "/opt/homebrew/bin/python3.11";

    /// <summary>Returns the venv's python interpreter path, provisioning the venv first if needed.</summary>
    public async Task<string> Install()
    {
        // webrtcvad has no prebuilt Windows wheel and compiles a native extension. Without the MSVC
        // toolchain that build is doomed, so fail fast with the fix hint instead of spending time on a
        // venv + multi-minute pip run that we already know ends in a compiler error.
        if (SetupDiagnostics.WindowsMissingMsvcBuildTools())
            throw new SetupException("Setup skipped: webrtcvad requires a C++ compiler that is not installed.",
                                     SetupDiagnostics.MsvcBuildToolsHint);

        string venv = Paths.ListenerVenv;
        Directory.CreateDirectory(Paths.ListenerData);
        Directory.CreateDirectory(Path.GetDirectoryName(venv)!);

        string venvPy = Path.Combine(venv, OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python");

        if (!Directory.Exists(venv))
        {
            logger?.LogInformation("Creating Listener virtual environment...");
            await RunExe(Python, $"-m venv \"{venv}\"", Paths.ListenerData);
        }

        string pip = Path.Combine(venv, OperatingSystem.IsWindows() ? @"Scripts\pip.exe" : "bin/pip");
        logger?.LogInformation("Installing Listener dependencies...");
        await RunExe(venvPy, "-m pip install -q --upgrade pip", Paths.ListenerData);

        if (File.Exists(Paths.ListenerRequirements))
            await RunExe(pip, $"install -q -r \"{Paths.ListenerRequirements}\"", Paths.ListenerData);
        else
            await RunExe(pip, "install -q faster-whisper websockets numpy webrtcvad \"setuptools<81\"", Paths.ListenerData);

        logger?.LogInformation("Listener environment ready.");
        return venvPy;
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

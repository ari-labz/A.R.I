using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

public class F5SetupService(string f5Path, ILogger? logger = null)
{
    private const string PYTHON      = "/opt/homebrew/bin/python3.11";
    private const string VENV_SUBDIR = "venv";
    private const string PACKAGES    = "f5-tts openai-whisper soundfile torch torchaudio flask sounddevice";

    public async Task Install()
    {
        string venv = Path.Combine(f5Path, VENV_SUBDIR);

        if (!Directory.Exists(f5Path))
            Directory.CreateDirectory(f5Path);

        if (!Directory.Exists(venv))
        {
            logger?.LogInformation("Creating F5-TTS virtual environment...");
            await Run(PYTHON, $"-m venv \"{venv}\"");
        }

        string pip = Path.Combine(venv, "bin", "pip");
        logger?.LogInformation("Installing F5-TTS dependencies (this may take several minutes)...");
        await Run(pip, $"install -q --upgrade {PACKAGES}");

        logger?.LogInformation("F5-TTS environment ready.");
    }

    private async Task Run(string exe, string args)
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

        Task stdoutTask = StreamLines(process.StandardOutput, line =>
        {
            if (!string.IsNullOrWhiteSpace(line))
                logger?.LogDebug("[pip] {Line}", line);
        });

        Task stderrTask = StreamLines(process.StandardError, line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
                logger?.LogDebug("[pip] {Line}", line);
            else
                logger?.LogWarning("[pip] {Line}", line);
        });

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new Exception($"Setup failed (exit {process.ExitCode}). Check logs above for details.");
    }

    private static async Task StreamLines(System.IO.StreamReader reader, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            onLine(line);
    }
}

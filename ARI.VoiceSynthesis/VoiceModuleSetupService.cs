using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

/// <summary>
/// Generic installer for any voice module that ships a setup.py at its root.
/// If the module directory does not exist, downloads the latest release from GitHub
/// using the repo mapping in voice-modules.json before running setup.
/// </summary>
public class VoiceModuleSetupService(string moduleName, ILogger? logger = null)
{
    private static string Python => OperatingSystem.IsWindows() ? "python" : "python3";

    public string ModuleDir => Path.Combine(Paths.VoiceModules, moduleName);

    private static readonly HttpClient http = new();

    public async Task Install()
    {
        if (!Directory.Exists(ModuleDir))
            await DownloadFromGitHub();

        string setupScript = Path.Combine(ModuleDir, "setup.py");
        if (!File.Exists(setupScript))
        {
            logger?.LogWarning("[{Module}] No setup.py found at {Path} — skipping install.", moduleName, setupScript);
            return;
        }

        logger?.LogInformation("[{Module}] Running setup.py install...", moduleName);
        await RunSetup(setupScript);
        logger?.LogInformation("[{Module}] Setup complete.", moduleName);
    }

    private async Task DownloadFromGitHub()
    {
        string? repo = LookupRepo();
        if (repo is null)
        {
            logger?.LogWarning("[{Module}] No GitHub repo mapping found — cannot auto-download.", moduleName);
            return;
        }

        logger?.LogInformation("[{Module}] Module not installed. Downloading latest release from {Repo}...", moduleName, repo);

        string apiUrl = $"https://api.github.com/repos/{repo}/releases/latest";
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("ARI", "1.0"));
        string json = await http.GetStringAsync(apiUrl);
        var release = JsonDocument.Parse(json);
        string tag = release.RootElement.GetProperty("tag_name").GetString()!;
        string tarball = $"https://github.com/{repo}/archive/refs/tags/{tag}.tar.gz";

        logger?.LogInformation("[{Module}] Downloading {Tag}...", moduleName, tag);

        string tempTar = Path.Combine(Path.GetTempPath(), $"ari-module-{moduleName}-{Guid.NewGuid():N}.tar.gz");
        try
        {
            await using (var stream = await http.GetStreamAsync(tarball))
            await using (var file = File.Create(tempTar))
                await stream.CopyToAsync(file);

            Directory.CreateDirectory(Path.GetDirectoryName(ModuleDir)!);

            string tempExtract = Path.Combine(Path.GetTempPath(), $"ari-module-{moduleName}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempExtract);

            var tar = Process.Start(new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf \"{tempTar}\" -C \"{tempExtract}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
            })!;
            await tar.WaitForExitAsync();

            // tar extracts to a single top-level folder like "StyleTTS2-v1.0.1"
            string[] extracted = Directory.GetDirectories(tempExtract);
            if (extracted.Length != 1)
                throw new Exception($"Expected one directory in archive, got {extracted.Length}");

            Directory.Move(extracted[0], ModuleDir);

            Directory.Delete(tempExtract, true);
            logger?.LogInformation("[{Module}] Installed {Tag} to {Path}.", moduleName, tag, ModuleDir);
        }
        finally
        {
            if (File.Exists(tempTar)) File.Delete(tempTar);
        }
    }

    private string? LookupRepo()
    {
        string registryPath = Path.Combine(Paths.BuildPath, "voice-modules.json");
        if (!File.Exists(registryPath))
        {
            registryPath = Path.Combine(AppContext.BaseDirectory, "voice-modules.json");
            if (!File.Exists(registryPath)) return null;
        }

        var registry = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(registryPath));
        return registry?.GetValueOrDefault(moduleName);
    }

    private async Task RunSetup(string setupScript)
    {
        ProcessStartInfo info = new()
        {
            FileName               = Python,
            Arguments              = $"\"{setupScript}\" install --module-dir \"{ModuleDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = ModuleDir,
        };

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException($"Failed to start setup for {moduleName}");

        StringBuilder captured = new();

        Task stdoutTask = StreamLines(process.StandardOutput, line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            captured.AppendLine(line);
            logger?.LogDebug("[{Module}] {Line}", moduleName, line);
        });

        Task stderrTask = StreamLines(process.StandardError, line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            captured.AppendLine(line);
            if (line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
                logger?.LogDebug("[{Module}] {Line}", moduleName, line);
            else
                logger?.LogWarning("[{Module}] {Line}", moduleName, line);
        });

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new SetupException(
                $"{moduleName} setup failed (exit {process.ExitCode})",
                SetupDiagnostics.Diagnose(captured.ToString()));
    }

    public string? FindVenvPython()
    {
        string venvPy = Path.Combine(ModuleDir, "venv",
            OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python3");
        return File.Exists(venvPy) ? venvPy : null;
    }

    private static async Task StreamLines(StreamReader reader, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            onLine(line);
    }
}

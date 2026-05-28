using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ARI.Core.Scripts;

public class Docker
{
    private readonly string composePath;
    private readonly string fullComposePath;
    private readonly string ollamaEndpoint;
    public static bool IsNativeOllamaInstall = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public static bool DockerLogging = true;

    public string? OllamaContainerName { get; private set; }

    public Docker(string composePath, string ollamaEndpoint)
    {
        this.composePath = composePath;
        this.fullComposePath = Path.GetFullPath(composePath);
        this.ollamaEndpoint = ollamaEndpoint;
    }

    public async Task IsRunning()
    {
        Process process = Common.RunCommand("docker", "info");
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            Common.Logger.LogError("Docker is not running. Please start Docker and try again.");
            throw new Exception("Docker is not running. Please start Docker and try again.");
        }

        Common.Logger.LogInformation("Docker is running.");
    }

    public async Task StartContainers()
    {
        if (!File.Exists(fullComposePath))
        {
            Common.Logger.LogError("compose.yaml not found at: {Path}", fullComposePath);
            throw new Exception($"compose.yaml not found at: {fullComposePath}");
        }

        if (IsNativeOllamaInstall)
        {
            Common.Logger.LogInformation("macOS detected. Ollama will be managed natively.");
            LocalOllama localOllama = new LocalOllama(ollamaEndpoint);
            await localOllama.IsRunning();
        }

        string scaleArgument = IsNativeOllamaInstall ? "--scale ollama=0" : "";

        ProcessStartInfo startInfo = new ProcessStartInfo("docker", $"compose -f {fullComposePath} up -d {scaleArgument}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        Process process = Process.Start(startInfo)
            ?? throw new Exception("Failed to start docker compose process.");

        process.ErrorDataReceived += (sender, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data)) return;
            if (DockerLogging)
                Common.Logger.LogInformation("[Docker] {Line}", args.Data);
        };

        process.BeginErrorReadLine();

        string stdout = await process.StandardOutput.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            Common.Logger.LogError("docker compose exited with a non-zero exit code.");
            if (!string.IsNullOrWhiteSpace(stdout))
                Common.Logger.LogError("stdout: {Output}", stdout);
            throw new Exception("Failed to start ARI containers. Check logs for details.");
        }

        Common.Logger.LogInformation("Containers are running.");

        await ResolveContainerNames();
    }

    public async Task StopContainers()
    {
        Common.Logger.LogInformation("Stopping containers...");

        ProcessStartInfo startInfo = new ProcessStartInfo("docker", $"compose -f {fullComposePath} down")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        Process process = Process.Start(startInfo)
            ?? throw new Exception("Failed to start docker compose down process.");

        process.ErrorDataReceived += (sender, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data)) return;
            if (DockerLogging)
                Common.Logger.LogInformation("[Docker] {Line}", args.Data);
        };

        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        Common.Logger.LogInformation("Containers stopped.");
    }

    /// <summary>
    /// Asks Docker for the actual container names after startup so we never
    /// have to guess or ask the user to provide them.
    /// </summary>
    private async Task ResolveContainerNames()
    {
        Process process = Common.RunCommand(
            "docker",
            $"compose -f {fullComposePath} ps --format json"
        );

        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            Common.Logger.LogWarning("Could not resolve container names from docker compose ps.");
            return;
        }

        // docker compose ps --format json returns one JSON object per line
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;

                if (!root.TryGetProperty("Service", out JsonElement serviceElement)) continue;
                if (!root.TryGetProperty("Name", out JsonElement nameElement)) continue;

                string service = serviceElement.GetString() ?? string.Empty;
                string name = nameElement.GetString() ?? string.Empty;

                if (service.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                {
                    OllamaContainerName = name;
                    Common.Logger.LogInformation("Ollama container name resolved: {Name}", name);
                }
            }
            catch (JsonException)
            {
                // skip malformed lines
            }
        }
    }
}
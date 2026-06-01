using System.Diagnostics;

namespace ARI.Core.Scripts;

public class Docker
{
    private readonly string fullComposePath;
    private readonly string envFilePath;

    public Docker(string composePath)
    {
        fullComposePath = Path.GetFullPath(composePath);
        envFilePath = Path.Combine(Path.GetDirectoryName(fullComposePath)!, ".env");
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

        string envFileArg = File.Exists(envFilePath) ? $"--env-file \"{envFilePath}\" " : string.Empty;
        ProcessStartInfo startInfo = new("docker", $"compose -f \"{fullComposePath}\" {envFileArg}up -d")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        Process process = Process.Start(startInfo)
            ?? throw new Exception("Failed to start docker compose process.");

        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
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
    }

    public async Task StopContainers()
    {
        Common.Logger.LogInformation("Stopping containers...");

        ProcessStartInfo startInfo = new("docker", $"compose -f {fullComposePath} stop trilium cloudflared")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        Process process = Process.Start(startInfo)
            ?? throw new Exception("Failed to start docker compose stop process.");

        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                Common.Logger.LogInformation("[Docker] {Line}", args.Data);
        };

        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        Common.Logger.LogInformation("Containers stopped.");
    }
}

using System.Diagnostics;

namespace ARI.Core.Scripts;

public class Docker
{
    private readonly string composePath;

    public Docker(string composePath)
    {
        this.composePath = composePath;
    }

    public async Task IsRunning()
    {
        Process process = Common.RunCommand("docker", "info");
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new Exception("Docker is not running. Please start Docker and try again.");

        Console.WriteLine("Docker is running.");
    }

    public async Task StartContainers()
    {
        if (!File.Exists(composePath))
            throw new Exception($"compose.yaml not found at: {composePath}");

        Process process = Common.RunCommand("docker", $"compose -f {composePath} up -d");
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new Exception("Failed to start ARI containers. Check Docker logs for details.");

        Console.WriteLine("Containers are running.");
    }
}
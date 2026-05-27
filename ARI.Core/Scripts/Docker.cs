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
        string fullComposePath = Path.GetFullPath(composePath);
        
        if (!File.Exists(fullComposePath))
        {
            Console.WriteLine($"compose.yaml not found at {fullComposePath}");
            throw new Exception($"compose.yaml not found at: {fullComposePath}");
        }

        Process process = Common.RunCommand("docker", $"compose -f {fullComposePath} up -d");
        await process.WaitForExitAsync();
        
        
        string output = await process.StandardOutput.ReadToEndAsync();
        string errors = await process.StandardError.ReadToEndAsync();

        if (process.ExitCode != 0)
        {
            Console.WriteLine($"docker exited with errors: {errors}");
            throw new Exception("Failed to start ARI containers. Check logs for details.");
        }

        Console.WriteLine("Containers are running.");
    }
}
using System.Diagnostics;
using System.Runtime.InteropServices;

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
        {
            Console.WriteLine("Docker is not running. Please start Docker and try again.");
            throw new Exception("Docker is not running. Please start Docker and try again.");
        }

        Console.WriteLine("Docker is running.");
    }

    public async Task StartContainers()
    {
        string fullComposePath = Path.GetFullPath(composePath);

        if (!File.Exists(fullComposePath))
        {
            Console.WriteLine($"compose.yaml not found at: {fullComposePath}");
            throw new Exception($"compose.yaml not found at: {fullComposePath}");
        }

        string scaleArgument = ShouldUseNativeOllama() ? "--scale ollama=0" : "";

        if (ShouldUseNativeOllama())
            Console.WriteLine("MacOS detected. Ollama will be managed natively. Skipping Docker Ollama.");

        Process process = Common.RunCommand("docker", $"compose -f {fullComposePath} up -d {scaleArgument}");

        string output = await process.StandardOutput.ReadToEndAsync();
        string errors = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        Console.WriteLine(output);

        if (process.ExitCode != 0)
        {
            Console.WriteLine($"docker exited with errors: {errors}");
            throw new Exception("Failed to start ARI containers. Check Docker logs for details.");
        }

        Console.WriteLine("Containers are running.");
    }

    public async Task StopContainers()
    {
        string fullComposePath = Path.GetFullPath(composePath);

        Console.WriteLine("Stopping containers...");

        Process process = Common.RunCommand("docker", $"compose -f {fullComposePath} down");
        await process.WaitForExitAsync();

        Console.WriteLine("Containers stopped.");
    }

    private bool ShouldUseNativeOllama()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    }
}
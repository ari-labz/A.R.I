using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ARI.Core.Scripts;

public class Ollama
{
    private readonly string model;

    public Ollama(string model)
    {
        this.model = model;
    }

    public async Task IsInstalled()
    {
        Process process = Common.RunCommand("ollama", "--version");
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            Console.WriteLine("Ollama not found. Installing...");
            await Install();
        }
        else
        {
            Console.WriteLine("Ollama is installed.");
        }
    }

    public async Task ModelExists()
    {
        Console.WriteLine($"Checking for model: {model}");

        Process process = Common.RunCommand("ollama", $"show {model}");
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            Console.WriteLine($"Model {model} not found. Pulling now, this may take a while...");
            await PullModel();
        }
        else
        {
            Console.WriteLine($"Model {model} is ready.");
        }
    }

    private async Task Install()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process process = Common.RunCommand("brew", "install ollama");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new Exception("Failed to install Ollama. Please install it manually from https://ollama.com");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Process process = Common.RunCommand("curl", "-fsSL https://ollama.com/install.sh | sh");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new Exception("Failed to install Ollama. Please install it manually from https://ollama.com");
        }
        else
        {
            throw new Exception("Automatic Ollama installation is not supported on this platform. Please install it manually from https://ollama.com");
        }

        Console.WriteLine("Ollama installed successfully.");
    }

    private async Task PullModel()
    {
        Process process = Common.RunCommand("ollama", $"pull {model}");
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new Exception($"Failed to pull model {model}. Check your internet connection and try again.");

        Console.WriteLine($"Model {model} pulled successfully.");
    }
}
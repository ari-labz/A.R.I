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
        try
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
        catch (Exception)
        {
            Console.WriteLine("Ollama not found. Installing...");
            await Install();
        }
    }

    public async Task ModelExists()
    {
        Console.WriteLine($"Checking for model: {model}");

        try
        {
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
        catch (Exception)
        {
            Console.WriteLine("Ollama is not installed. Cannot check for model.");
            throw new Exception("Ollama is not installed. Cannot check for model.");
        }
    }

    private async Task Install()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                Process process = Common.RunCommand("curl", "-fsSL https://ollama.com/install.sh | sh");
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    Console.WriteLine("Failed to install Ollama. Please install it manually from https://ollama.com");
                    throw new Exception("Failed to install Ollama. Please install it manually from https://ollama.com");
                }
            }
            catch (Exception)
            {
                Console.WriteLine("curl is not installed. Please install Ollama manually from https://ollama.com");
                throw new Exception("curl is not installed. Please install Ollama manually from https://ollama.com");
            }
        }
        else
        {
            Console.WriteLine("Automatic Ollama installation is not supported on this platform. Please install it manually from https://ollama.com");
            throw new Exception("Automatic Ollama installation is not supported on this platform. Please install it manually from https://ollama.com");
        }

        Console.WriteLine("Ollama installed successfully.");
    }

    private async Task PullModel()
    {
        Process process = Common.RunCommand("ollama", $"pull {model}");
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            Console.WriteLine($"Failed to pull model {model}. Check your internet connection and try again.");
            throw new Exception($"Failed to pull model {model}. Check your internet connection and try again.");
        }

        Console.WriteLine($"Model {model} pulled successfully.");
    }
}
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ARI.Core.Scripts;

public class Ollama
{
    private readonly string endpoint;
    private readonly string model;
    public bool isNativeInstall;

    public Ollama(string endpoint, string model)
    {
        this.endpoint = endpoint;
        this.model = model;
        isNativeInstall = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    }

    public async Task IsInstalled()
    {
        if (isNativeInstall)
        {
            await EnsureNativeOllamaIsRunning();
            return;
        }

        Console.WriteLine("Ollama is running via Docker.");
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
        catch (System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("Ollama is not installed. Cannot check for model.");
            throw new Exception("Ollama is not installed. Cannot check for model.");
        }
    }

    private async Task EnsureNativeOllamaIsRunning()
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
        catch (System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("Ollama not found. Installing...");
            await Install();
        }

        if (!await IsResponding())
        {
            Console.WriteLine("Starting Ollama natively...");
            Common.RunCommand("ollama", "serve");
            await WaitUntilReady();
        }
        else
        {
            Console.WriteLine("Ollama is already running.");
        }
    }

    private async Task<bool> IsResponding()
    {
        try
        {
            using HttpClient httpClient = new HttpClient();
            HttpResponseMessage response = await httpClient.GetAsync(endpoint);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private async Task WaitUntilReady()
    {
        Console.WriteLine("Waiting for Ollama to come online...");

        using HttpClient httpClient = new HttpClient();
        DateTime timeout = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < timeout)
        {
            try
            {
                HttpResponseMessage response = await httpClient.GetAsync(endpoint);

                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // not ready yet, keep waiting
            }

            await Task.Delay(500);
        }

        Console.WriteLine("Ollama did not come online within 30 seconds.");
        throw new Exception("Ollama did not come online within 30 seconds.");
    }

    private async Task Install()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                Process process = Common.RunCommand("brew", "install ollama");
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    Console.WriteLine("Failed to install Ollama. Please install it manually from https://ollama.com");
                    throw new Exception("Failed to install Ollama. Please install it manually from https://ollama.com");
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.WriteLine("Homebrew is not installed. Please install Ollama manually from https://ollama.com");
                throw new Exception("Homebrew is not installed. Please install Ollama manually from https://ollama.com");
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
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
            catch (System.ComponentModel.Win32Exception)
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
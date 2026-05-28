using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ARI.Core.Scripts;

public class LocalOllama
{
    private readonly string endpoint;

    public LocalOllama(string endpoint)
    {
        this.endpoint = endpoint;
    }

    /// <summary>
    /// checks that ollama is running,
    /// if not, start ollama
    /// </summary>
    public async Task IsRunning()
    {
        await IsInstalled();

        if (!await IsResponding())
        {
            Common.Logger.LogInformation("Starting Ollama natively...");
            Common.RunCommand("ollama", "serve");
            await WaitUntilReady();
        }
        else
        {
            Common.Logger.LogInformation("Ollama is already running.");
        }
    }

    /// <summary>
    /// Checks that ollama is installed,
    /// if not, Install ollama
    /// </summary>
    private async Task IsInstalled()
    {
        try
        {
            Process process = Common.RunCommand("ollama", "--version");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                Common.Logger.LogInformation("Ollama not found. Installing...");
                await Install();
            }
            else
            {
                Common.Logger.LogInformation("Ollama is installed.");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Common.Logger.LogInformation("Ollama not found. Installing...");
            await Install();
        }
    }

    /// <summary>
    /// Attempts to automatically install Ollama,
    /// if this fails, instructs the user to install it manually from the website
    /// </summary>
    private async Task Install()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new InvalidOperationException("LocalOllama.Install() should only be called on macOS.");

        try
        {
            Common.Logger.LogInformation("Installing Ollama via Homebrew...");
            Process process = Common.RunCommand("brew", "install ollama");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                Common.Logger.LogError("Failed to install Ollama via Homebrew.");
                throw new Exception("Failed to install Ollama. Please install it manually from https://ollama.com");
            }

            Common.Logger.LogInformation("Ollama installed successfully.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Common.Logger.LogError("Cannot auto-install Ollama on macOS. Please install Ollama manually from https://ollama.com");
            throw new Exception("Homebrew is not installed. Please install Ollama manually from https://ollama.com");
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
        Common.Logger.LogInformation("Waiting for Ollama to come online...");

        using HttpClient httpClient = new HttpClient();
        DateTime timeout = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < timeout)
        {
            try
            {
                HttpResponseMessage response = await httpClient.GetAsync(endpoint);
                if (response.IsSuccessStatusCode)
                {
                    Common.Logger.LogInformation("Ollama is online.");
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // not ready yet, keep waiting
            }

            await Task.Delay(500);
        }

        Common.Logger.LogError("Ollama did not come online within 30 seconds.");
        throw new Exception("Ollama did not come online within 30 seconds.");
    }
}
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ARI.Core.Scripts;

public class Ollama
{
    private readonly string endpoint;
    private readonly string model;
    private readonly string? containerName;
    private readonly bool isNative = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public Ollama(string endpoint, string model, string? containerName = null)
    {
        this.endpoint = endpoint;
        this.model = model;
        this.containerName = containerName;
    }

    public async Task IsRunning()
    {
        Common.Logger.LogInformation("Checking Ollama is running...");

        try
        {
            using HttpClient httpClient = new HttpClient();
            HttpResponseMessage response = await httpClient.GetAsync(endpoint);

            if (!response.IsSuccessStatusCode)
            {
                Common.Logger.LogError("Ollama is not responding. Something went wrong during startup.");
                throw new Exception("Ollama is not responding. Check Docker logs for details.");
            }

            Common.Logger.LogInformation("Ollama is running.");
        }
        catch (HttpRequestException)
        {
            Common.Logger.LogError("Ollama is not reachable at {Endpoint}.", endpoint);
            throw new Exception($"Ollama is not reachable at {endpoint}. Check Docker logs for details.");
        }
    }

    public async Task IsModelInstalled()
    {
        Common.Logger.LogInformation("Checking for model: {Model}", model);

        try
        {
            Process process = RunOllamaCommand($"show {model}");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                Common.Logger.LogInformation("Model {Model} not found. Pulling now, this may take a while...", model);
                await PullModelWithCorruptionHandling();
            }
            else
            {
                Common.Logger.LogInformation("Model {Model} is ready.", model);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Common.Logger.LogError("Ollama CLI is not available. Cannot check for model.");
            throw new Exception("Ollama CLI is not available. Cannot check for model.");
        }
    }

    /// <summary>
    /// Runs an ollama command either natively (macOS) or inside the Docker container (Windows/Linux).
    /// </summary>
    private Process RunOllamaCommand(string ollamaArgs)
    {
        if (isNative)
            return Common.RunCommand("ollama", ollamaArgs);

        if (string.IsNullOrWhiteSpace(containerName))
            throw new Exception("ContainerName must be set in AriConfig.json when running Ollama via Docker.");

        return Common.RunCommand("docker", $"exec {containerName} ollama {ollamaArgs}");
    }

    private async Task PullModelWithCorruptionHandling()
    {
        try
        {
            await PullModel();
        }
        catch (Exception ex) when (ex.Message.Contains("EOF") || ex.Message.Contains("unexpected end"))
        {
            Common.Logger.LogWarning("Model appears corrupted. Deleting and retrying once...");
            await DeleteModel();

            try
            {
                await PullModel();
            }
            catch (Exception)
            {
                Common.Logger.LogError("Model re-download failed after corruption recovery. Check your connection.");
                throw;
            }
        }
    }

    private async Task DeleteModel()
    {
        Process process = RunOllamaCommand($"rm {model}");
        await process.WaitForExitAsync();
        Common.Logger.LogInformation("Deleted corrupted model: {Model}", model);
    }

    private async Task PullModel()
    {
        Process process = RunOllamaCommand($"pull {model}");
        //await process.WaitForExitAsync();

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(
            stdout,
            stderr,
            process.WaitForExitAsync()
        );

        if (process.ExitCode != 0)
        {
            Common.Logger.LogError("Failed to pull model {Model}.", model);
            throw new Exception($"Failed to pull model {model}. Check your internet connection and try again.");
        }

        Common.Logger.LogInformation("Model {Model} pulled successfully.", model);
    }
}
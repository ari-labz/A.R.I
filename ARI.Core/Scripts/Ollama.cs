using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ARI.Core.Scripts;

public class Ollama
{
    private static readonly Regex ANSI_CODES = new Regex(@"\x1b\[[0-9;?]*[a-zA-Z]|\x1b[^[]|\[[0-9;?]*[a-zA-Z]", RegexOptions.Compiled);
    private static readonly Regex PERCENT_PATTERN = new Regex(@"(\d+)%", RegexOptions.Compiled);

    private readonly string endpoint;
    private readonly string? containerName;
    private readonly bool isNative = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public Ollama(string endpoint, string? containerName = null)
    {
        this.endpoint = endpoint;
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

    public async Task IsModelInstalled(string model)
    {
        Common.Logger.LogInformation("Checking for model: {Model}", model);

        try
        {
            Process process = RunOllamaCommand($"show {model}");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                Common.Logger.LogInformation("Model {Model} not found. Pulling now, this may take a while...", model);
                await PullModelWithCorruptionHandling(model);
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

    private async Task PullModelWithCorruptionHandling(string model)
    {
        try
        {
            await PullModel(model);
        }
        catch (Exception ex) when (ex.Message.Contains("EOF") || ex.Message.Contains("unexpected end"))
        {
            Common.Logger.LogWarning("Model appears corrupted. Deleting and retrying once...");
            await DeleteModel(model);

            try
            {
                await PullModel(model);
            }
            catch (Exception)
            {
                Common.Logger.LogError("Model re-download failed after corruption recovery. Check your connection.");
                throw;
            }
        }
    }

    private async Task DeleteModel(string model)
    {
        Process process = RunOllamaCommand($"rm {model}");
        await process.WaitForExitAsync();
        Common.Logger.LogInformation("Deleted corrupted model: {Model}", model);
    }

    private async Task PullModel(string model)
    {
        string command = isNative ? "ollama" : "docker";
        string arguments = isNative ? $"pull {model}" : $"exec {containerName} ollama pull {model}";

        ProcessStartInfo processInfo = new ProcessStartInfo(command, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        Process process = Process.Start(processInfo)
            ?? throw new Exception($"Failed to start process: {command} {arguments}");

        await Task.WhenAll(
            ForwardOutputToLog(process.StandardOutput, model),
            ForwardOutputToLog(process.StandardError, model),
            process.WaitForExitAsync()
        );

        if (process.ExitCode != 0)
        {
            Common.Logger.LogError("Failed to pull model {Model}.", model);
            throw new Exception($"Failed to pull model {model}. Check your internet connection and try again.");
        }

        Common.Logger.LogInformation("Model {Model} pulled successfully.", model);
    }

    private static async Task ForwardOutputToLog(StreamReader reader, string model)
    {
        int lastLoggedPercent = -1;

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            string cleaned = ANSI_CODES.Replace(line, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleaned)) continue;

            // Overwrite the current console line in place for smooth progress display
            Console.Write($"\r  pulling {model}: {cleaned}    ");

            // Only write to the log file when the percentage advances
            Match match = PERCENT_PATTERN.Match(cleaned);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int percent) && percent != lastLoggedPercent)
            {
                Common.Logger.LogInformation("[pull] {Model}: {Percent}%", model, percent);
                lastLoggedPercent = percent;
            }
        }

        Console.WriteLine();
    }
}

using ARI.Common;
using System.Diagnostics;

namespace ARI.Core.Scripts;

public class Dependency
{
    private static readonly string[] BrewPaths = ["/opt/homebrew/bin", "/usr/local/bin"];

    public static async Task CheckDocker()
    {
        try
        {
            Process process = Shared.RunCommand("docker", "--version");
            await process.WaitForExitAsync();
            Shared.Logger.LogInformation("Docker is installed.");
        }
        catch
        {
            throw new Exception("Docker is not installed. Please install Docker Desktop from https://docker.com and try again.");
        }
    }

    public static async Task CheckPython()
    {
        try
        {
            Process process = Shared.RunCommand("python3", "--version");
            await process.WaitForExitAsync();
            Shared.Logger.LogInformation("Python is installed.");
        }
        catch
        {
            throw new Exception("Python is not installed. Please install Python and try again.");
        }
    }

    public static async Task CheckHomebrew()
    {
        EnsureBrewInPath();
        if (await CommandExistsAsync("brew"))
        {
            Shared.Logger.LogInformation("Homebrew is installed.");
            return;
        }

        Shared.Logger.LogInformation("Homebrew not found. Attempting to install...");

        string scriptPath = Path.GetTempFileName() + ".sh";
        try
        {
            using HttpClient hc = new();
            string script = await hc.GetStringAsync("https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh");
            await File.WriteAllTextAsync(scriptPath, script);
        }
        catch (Exception ex)
        {
            throw new Exception(
                $"Could not download Homebrew install script: {ex.Message}\n" +
                "Please install Homebrew manually: https://brew.sh");
        }

        Process? proc = Process.Start(new ProcessStartInfo("/bin/bash", scriptPath)
        {
            UseShellExecute = false,
        });

        if (proc is not null)
        {
            await proc.WaitForExitAsync();
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
            EnsureBrewInPath();
            if (proc.ExitCode == 0 && await CommandExistsAsync("brew")) return;
        }

        throw new Exception(
            "Homebrew could not be installed automatically.\n" +
            "Please install it manually: https://brew.sh");
    }

    public static async Task CheckLlamaCpp()
    {
        if (await CommandExistsAsync("llama-server"))
        {
            Shared.Logger.LogInformation("llama-server is installed.");
            return;
        }

        Shared.Logger.LogInformation("llama.cpp not found. Installing via Homebrew...");

        Process process = Process.Start(new ProcessStartInfo("brew", "install llama.cpp")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new Exception("Failed to start brew install.");

        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Shared.Logger.LogInformation("[brew] {Line}", e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Shared.Logger.LogInformation("[brew] {Line}", e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0 || !await CommandExistsAsync("llama-server"))
            throw new Exception("Failed to install llama.cpp. Please run: brew install llama.cpp");
    }

    private static void EnsureBrewInPath()
    {
        string current = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in BrewPaths)
            if (!current.Contains(dir))
                Environment.SetEnvironmentVariable("PATH", $"{dir}:{current}");
    }

    private static async Task<bool> CommandExistsAsync(string cmd)
    {
        try
        {
            Process p = Process.Start(new ProcessStartInfo("which", cmd)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}

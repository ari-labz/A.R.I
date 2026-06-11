using System.Diagnostics;

namespace ARI.Core.Scripts;

public class LocalLlamaServer : IDisposable
{
    private readonly LlamaServerConfig server;
    private readonly LlamaModelConfig  model;
    private readonly string            modelsPath;
    private Process? serverProcess;

    public int Pid { get; private set; } = -1;

    public LocalLlamaServer(LlamaServerConfig server, LlamaModelConfig model, string executableDirectory)
    {
        this.server = server;
        this.model  = model;
        modelsPath  = Path.IsPathRooted(model.ModelsPath)
            ? model.ModelsPath
            : Path.GetFullPath(Path.Combine(executableDirectory, model.ModelsPath));
    }

    public async Task IsReady()
    {
        await InstallHomebrew();
        await InstallLlamaCpp();
        Directory.CreateDirectory(modelsPath);
        await InstallModelFiles(model.File);
        if (!string.IsNullOrWhiteSpace(model.MmprojFile))
            await InstallModelFiles(model.MmprojFile);
        await StartServer();
        await WaitUntilReady();
    }

    public void Stop()
    {
        if (serverProcess is null || serverProcess.HasExited) return;
        Common.Logger.LogInformation("Stopping llama-server...");
        serverProcess.Kill(entireProcessTree: true);
        serverProcess.WaitForExit(5000);
        Common.Logger.LogInformation("llama-server stopped.");
    }

    public void Dispose()
    {
        Stop();
        serverProcess?.Dispose();
    }

    // ── Brew ─────────────────────────────────────────────────────────────────

    // Known Homebrew binary locations on Apple Silicon and Intel Macs
    private static readonly string[] BrewPaths = ["/opt/homebrew/bin", "/usr/local/bin"];

    private static void EnsureHomebrewInPath()
    {
        string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in BrewPaths)
        {
            if (!currentPath.Contains(dir))
                Environment.SetEnvironmentVariable("PATH", $"{dir}:{currentPath}");
        }
    }

    private async Task InstallHomebrew()
    {
        EnsureHomebrewInPath();

        if (await CommandExists("brew"))
        {
            Common.Logger.LogInformation("Homebrew is installed.");
            return;
        }

        Common.Logger.LogInformation("Homebrew not found. Attempting to install...");

        // Download the install script to a temp file and execute it so the user can authenticate interactively.
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
                $"Could not download the Homebrew install script: {ex.Message}\n" +
                "Please install Homebrew manually: https://brew.sh\n" +
                "Then restart ARI.");
        }

        ProcessStartInfo info = new("/bin/bash", scriptPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        Process? proc = Process.Start(info);
        if (proc != null)
        {
            await proc.WaitForExitAsync();
            if (File.Exists(scriptPath)) File.Delete(scriptPath);

            EnsureHomebrewInPath();
            if (proc.ExitCode == 0 && await CommandExists("brew"))
            {
                Common.Logger.LogInformation("Homebrew installed successfully.");
                return;
            }
        }

        throw new Exception(
            "Homebrew is not installed and could not be installed automatically.\n" +
            "Please install it manually: https://brew.sh\n" +
            "Then restart ARI.");
    }

    // ── llama.cpp ─────────────────────────────────────────────────────────────

    private async Task InstallLlamaCpp()
    {
        if (await CommandExists("llama-server"))
        {
            Common.Logger.LogInformation("llama-server is installed.");
            return;
        }

        Common.Logger.LogInformation("llama.cpp not found. Installing via Homebrew (this may take a few minutes)...");

        ProcessStartInfo info = new("brew", "install llama.cpp")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Process process = Process.Start(info)
            ?? throw new Exception("Failed to start brew install process.");

        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Common.Logger.LogInformation("[brew] {Line}", e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Common.Logger.LogInformation("[brew] {Line}", e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0 || !await CommandExists("llama-server"))
            throw new Exception(
                "Failed to install llama.cpp via Homebrew.\n" +
                "Please install it manually: brew install llama.cpp\n" +
                "Then restart ARI.");

        Common.Logger.LogInformation("llama.cpp installed successfully.");
    }

    // ── Model files ───────────────────────────────────────────────────────────

    private async Task InstallModelFiles(string filename)
    {
        string destPath = Path.Combine(modelsPath, filename);
        if (File.Exists(destPath))
        {
            Common.Logger.LogInformation("Model file found: {File}", filename);
            return;
        }

        string url = $"{model.DownloadBaseUrl}/{filename}";
        Common.Logger.LogInformation("Downloading {File} — this may take a while...", filename);
        await DownloadFile(url, destPath, filename);
        Common.Logger.LogInformation("Download complete: {File}", filename);
    }

    private static async Task DownloadFile(string url, string destPath, string label)
    {
        using HttpClient httpClient = new HttpClient();
        httpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        httpClient.DefaultRequestHeaders.Add("User-Agent", "ARI/1.0");

        using HttpResponseMessage response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        string tempPath = destPath + ".tmp";

        try
        {
            await using Stream src  = await response.Content.ReadAsStreamAsync();
            await using Stream dest = File.Create(tempPath);

            byte[] buffer = new byte[81920];
            long downloaded = 0;
            int lastLoggedPercent = -1;
            int read;

            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read));
                downloaded += read;

                if (totalBytes > 0)
                {
                    int percent = (int)(downloaded * 100 / totalBytes.Value);
                    if (percent != lastLoggedPercent && percent % 5 == 0)
                    {
                        Common.Logger.LogInformation("[download] {Label}: {Percent}% ({MB:F0} MB)", label, percent, downloaded / 1_048_576.0);
                        lastLoggedPercent = percent;
                    }
                }
            }

            File.Move(tempPath, destPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    // ── Server process ────────────────────────────────────────────────────────

    private Task StartServer()
    {
        string modelPath = Path.Combine(modelsPath, model.File);

        List<string> argParts = new()
        {
            $"-m \"{modelPath}\"",
            "--cache-type-k q4_0 --cache-type-v q4_0",
            $"-c {server.ContextSize}",
            "--cache-ram 0",
            "--n-predict -1",
            "--temp 0.7 --top-p 0.80 --top-k 20 --repeat-penalty 1.0",
            $"-np {server.ParallelSlots} -ngl 99 --port {server.Port}",
            "--host 127.0.0.1"
        };

        if (!string.IsNullOrWhiteSpace(model.MmprojFile))
            argParts.Insert(1, $"--mmproj \"{Path.Combine(modelsPath, model.MmprojFile)}\"");

        if (model.UseMtp)
            argParts.Insert(1, "--spec-type draft-mtp --spec-draft-n-max 3");

        string args = string.Join(" ", argParts);

        ProcessStartInfo info = new("llama-server", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        serverProcess = Process.Start(info)
            ?? throw new Exception("Failed to start llama-server process.");

        Common.Logger.LogInformation("llama-server started (PID {Pid}).", serverProcess.Id);
        Pid = serverProcess.Id;
        return Task.CompletedTask;
    }

    private async Task WaitUntilReady()
    {
        Common.Logger.LogInformation("Waiting for llama-server to come online...");

        using HttpClient httpClient = new HttpClient();
        DateTime timeout = DateTime.UtcNow.AddMinutes(3);

        while (DateTime.UtcNow < timeout)
        {
            if (serverProcess?.HasExited == true)
                throw new Exception("llama-server exited unexpectedly during startup. Check logs for details.");

            try
            {
                HttpResponseMessage response = await httpClient.GetAsync($"{server.Endpoint}/health");
                if (response.IsSuccessStatusCode)
                {
                    Common.Logger.LogInformation("llama-server is online.");
                    return;
                }
            }
            catch (HttpRequestException) { }

            await Task.Delay(1000);
        }

        throw new Exception("llama-server did not come online within 3 minutes.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<bool> CommandExists(string command)
    {
        try
        {
            ProcessStartInfo info = new("which", command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            Process process = Process.Start(info)!;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

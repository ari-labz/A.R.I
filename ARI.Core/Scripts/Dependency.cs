using ARI.Common;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ARI.Core.Scripts;

public class Dependency
{
    private static readonly string[] BrewPaths = ["/opt/homebrew/bin", "/usr/local/bin"];

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

    /// <summary>
    /// Ensures a usable llama-server and records its path in <see cref="Shared.LlamaServer"/>.
    /// Resolution order: any llama-server already on PATH (a user's own GPU build always wins) →
    /// a build we downloaded on a previous run → a fresh install. macOS installs via Homebrew;
    /// Windows/Linux download a prebuilt binary from llama.cpp's GitHub releases.
    /// </summary>
    public static async Task CheckLlamaCpp()
    {
        if (await CommandExistsAsync("llama-server"))
        {
            Shared.LlamaServer = "llama-server";
            Shared.Logger.LogInformation("llama-server found on PATH.");
            return;
        }

        string? managed = FindManagedLlamaServer();
        if (managed is not null)
        {
            Shared.LlamaServer = managed;
            Shared.Logger.LogInformation("Using managed llama-server: {Path}", managed);
            return;
        }

        Shared.Logger.LogInformation("llama-server not found. Installing...");

        switch (0)
        {
            case 0 when OperatingSystem.IsMacOS():
                await InstallLlamaViaBrew();
                Shared.LlamaServer = "llama-server";
                break;
            case 0 when OperatingSystem.IsWindows():
            case 0 when OperatingSystem.IsLinux():
            default:
                Shared.LlamaServer = await DownloadLlamaPrebuilt();
                break;
        }
    }

    private static async Task InstallLlamaViaBrew()
    {
        Shared.Logger.LogInformation("Installing llama.cpp via Homebrew...");

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

    // Downloads a prebuilt llama.cpp release into the managed tools dir and returns the full path
    // to llama-server. Prefers a Vulkan (broad cross-vendor GPU) build, falling back to CPU. A user
    // wanting CUDA/ROCm/Metal should install llama.cpp themselves — a llama-server on PATH always
    // takes precedence over this download.
    private static async Task<string> DownloadLlamaPrebuilt()
    {
        string toolsDir = Paths.ServerDir(Path.Combine("tools", "llama.cpp"));
        Directory.CreateDirectory(toolsDir);

        using HttpClient hc = new();
        hc.DefaultRequestHeaders.UserAgent.ParseAdd("ARI-Server/1.0");

        Shared.Logger.LogInformation("Fetching latest llama.cpp release...");
        string json = await hc.GetStringAsync("https://api.github.com/repos/ggml-org/llama.cpp/releases/latest");
        using JsonDocument doc = JsonDocument.Parse(json);

        string? url = SelectAsset(doc.RootElement.GetProperty("assets"));
        if (url is null)
            throw new Exception(
                "No prebuilt llama.cpp binary matched this platform. " +
                "Please install llama.cpp manually and ensure 'llama-server' is on your PATH.");

        string zipPath = Path.Combine(toolsDir, "llama.zip");
        Shared.Logger.LogInformation("Downloading llama.cpp: {Url}", url);
        await using (Stream s = await hc.GetStreamAsync(url))
        await using (FileStream fs = File.Create(zipPath))
            await s.CopyToAsync(fs);

        Shared.Logger.LogInformation("Extracting llama.cpp...");
        ZipFile.ExtractToDirectory(zipPath, toolsDir, overwriteFiles: true);
        File.Delete(zipPath);

        string exe = FindManagedLlamaServer()
            ?? throw new Exception("llama-server not found inside the downloaded llama.cpp archive.");

        if (!OperatingSystem.IsWindows())
        {
            Process chmod = Process.Start(new ProcessStartInfo("chmod", $"+x \"{exe}\"") { UseShellExecute = false })!;
            await chmod.WaitForExitAsync();
        }

        Shared.Logger.LogInformation("llama-server ready: {Path}", exe);
        return exe;
    }

    // Picks the best-matching release asset for this OS/arch. Naming follows llama.cpp's convention,
    // e.g. llama-b<build>-bin-win-vulkan-x64.zip / llama-b<build>-bin-ubuntu-vulkan-x64.zip.
    private static string? SelectAsset(JsonElement assets)
    {
        string os   = OperatingSystem.IsWindows() ? "win" : "ubuntu";
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

        List<(string Name, string Url)> candidates = new();
        foreach (JsonElement a in assets.EnumerateArray())
        {
            string name = (a.GetProperty("name").GetString() ?? "").ToLowerInvariant();
            string url  = a.GetProperty("browser_download_url").GetString() ?? "";
            if (name.EndsWith(".zip") && name.Contains($"-{os}-") && name.Contains(arch))
                candidates.Add((name, url));
        }

        // Prefer a GPU (Vulkan) build, then CPU, then anything matching the platform.
        foreach (string backend in new[] { "vulkan", "cpu", "" })
        {
            (string Name, string Url) match = candidates.FirstOrDefault(c => backend.Length == 0 || c.Name.Contains(backend));
            if (!string.IsNullOrEmpty(match.Url)) return match.Url;
        }
        return null;
    }

    private static string LlamaServerFileName() =>
        OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

    private static string? FindManagedLlamaServer()
    {
        string toolsDir = Paths.ServerDir(Path.Combine("tools", "llama.cpp"));
        if (!Directory.Exists(toolsDir)) return null;
        return Directory.EnumerateFiles(toolsDir, LlamaServerFileName(), SearchOption.AllDirectories).FirstOrDefault();
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
            string finder = OperatingSystem.IsWindows() ? "where" : "which";
            Process p = Process.Start(new ProcessStartInfo(finder, cmd)
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

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

    // Homebrew is OPTIONAL: it's only used to install llama.cpp (which now falls back to a prebuilt
    // download when brew is absent) and espeak-ng for voice (which degrades on its own). So this only
    // detects brew and puts it on PATH — it never installs it (the official installer needs an
    // interactive sudo/TTY that a GUI-launched app can't provide) and never aborts startup.
    public static Task CheckHomebrew()
    {
        EnsureBrewInPath();
        if (FindBrew() is not null)
            Shared.Logger.LogInformation("Homebrew is installed.");
        else
            Shared.Logger.LogWarning(
                "Homebrew not found — it's optional (only needed for espeak-ng voice). " +
                "Install it from https://brew.sh if you want voice. Continuing without it.");
        return Task.CompletedTask;
    }

    private static string? FindBrew() => FindInBrewBins("brew");

    // Locates a binary in the Homebrew bin dirs (Apple Silicon → /opt/homebrew/bin, Intel →
    // /usr/local/bin) by absolute path, which is reliable even for a Finder-launched app that
    // doesn't inherit a login PATH.
    private static string? FindInBrewBins(string name)
    {
        foreach (string dir in BrewPaths)
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Ensures a usable llama-server and records its path in <see cref="Shared.LlamaServer"/>.
    /// Resolution order: any llama-server already on PATH (a user's own GPU build always wins) →
    /// a build we downloaded on a previous run → a fresh install. macOS installs via Homebrew;
    /// Windows/Linux download a prebuilt binary from llama.cpp's GitHub releases.
    /// </summary>
    public static async Task CheckLlamaCpp()
    {
        // A Finder-launched app gets a stripped PATH without /opt/homebrew/bin, so `which` misses a
        // brew-installed llama-server that a dev shell finds fine. Check the brew bins directly first,
        // then fall back to a PATH lookup (resolved to an absolute path, since we can't spawn a bare
        // command name off a login PATH we never inherited).
        string? existing = FindInBrewBins("llama-server") ?? await ResolveCommandPath("llama-server");
        if (existing is not null)
        {
            Shared.LlamaServer = existing;
            Shared.Logger.LogInformation("Using existing llama-server: {Path}", existing);
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
                // Prefer brew when it's available; otherwise fall back to a prebuilt download so the
                // server still runs on machines without Homebrew (e.g. non-admin accounts).
                string? brew = FindBrew();
                string? viaBrew = brew is not null ? await TryInstallLlamaViaBrew(brew) : null;
                if (viaBrew is null && brew is null)
                    Shared.Logger.LogInformation("Homebrew not available — downloading a prebuilt llama.cpp instead.");
                Shared.LlamaServer = viaBrew ?? await DownloadLlamaPrebuilt();
                break;
            case 0 when OperatingSystem.IsWindows():
            case 0 when OperatingSystem.IsLinux():
            default:
                Shared.LlamaServer = await DownloadLlamaPrebuilt();
                break;
        }
    }

    // Installs llama.cpp with Homebrew and returns the absolute path to the installed llama-server,
    // or null if the install fails (caller falls back to a prebuilt download). Uses brew's absolute
    // path — a Finder-launched app can't resolve the bare "brew" name off a login PATH it never got.
    private static async Task<string?> TryInstallLlamaViaBrew(string brewPath)
    {
        Shared.Logger.LogInformation("Installing llama.cpp via Homebrew...");
        try
        {
            Process process = Process.Start(new ProcessStartInfo(brewPath, "install llama.cpp")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new Exception("Failed to start brew.");

            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Shared.Logger.LogInformation("[brew] {Line}", e.Data); };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Shared.Logger.LogInformation("[brew] {Line}", e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            // brew installs into its own bin dir (same dir as the brew binary).
            string llamaServer = Path.Combine(Path.GetDirectoryName(brewPath)!, "llama-server");
            if (process.ExitCode == 0 && File.Exists(llamaServer))
                return llamaServer;

            Shared.Logger.LogWarning("Homebrew install of llama.cpp did not succeed — falling back to a prebuilt download.");
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("Homebrew install of llama.cpp failed ({Error}) — falling back to a prebuilt download.", ex.Message);
        }
        return null;
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
        string os   = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "macos" : "ubuntu";
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

    // Returns the absolute path of a command as resolved by which/where, or null if not found.
    private static async Task<string?> ResolveCommandPath(string cmd)
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
            string outp = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            if (p.ExitCode == 0 && outp.Length > 0)
                return outp.Split('\n')[0].Trim();   // `where` can list multiple matches
            return null;
        }
        catch { return null; }
    }

}

using ARI.Common;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ARI.Core.Scripts;

public class Dependency
{
    private static readonly string[] BrewPaths = ["/opt/homebrew/bin", "/usr/local/bin"];
    private static string ConfigPath => Path.Combine(Paths.PersistentData, "llamacpp.json");

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

    public static Task CheckHomebrew()
    {
        EnsureBrewInPath();
        bool found = File.Exists("/opt/homebrew/bin/brew") || File.Exists("/usr/local/bin/brew");
        if (found)
            Shared.Logger.LogInformation("Homebrew is installed.");
        else
            Shared.Logger.LogWarning(
                "Homebrew not found — it's optional (only needed for espeak-ng voice). " +
                "Install it from https://brew.sh if you want voice. Continuing without it.");
        return Task.CompletedTask;
    }

    // ── llama.cpp ────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures a usable llama-server is available. Checks saved config, then detects existing
    /// installs, then downloads a prebuilt release. Updates Shared.LlamaServer and Shared.LlamaCpp.
    /// </summary>
    public static async Task CheckLlamaCpp()
    {
        EnsureBrewInPath();
        LlamaCppStatus cfg = LoadConfig();

        // 1. Saved path from previous run
        if (!string.IsNullOrEmpty(cfg.InstallPath))
        {
            string? saved = FindLlamaServerIn(cfg.InstallPath);
            if (saved is not null)
            {
                Shared.LlamaServer = saved;
                Shared.LlamaCpp = cfg;
                Shared.Logger.LogInformation("Using configured llama-server: {Path}", saved);
                _ = CheckForUpdate(cfg);
                return;
            }
            Shared.Logger.LogWarning("Saved llama.cpp path no longer valid: {Path}", cfg.InstallPath);
        }

        // 2. Detect existing installations
        string? existing = await DetectExisting();
        if (existing is not null)
        {
            string dir = ResolveInstallDir(existing);
            Shared.LlamaServer = existing;
            cfg.InstallPath = dir;
            cfg.InstalledVersion = await GetVersion(existing);
            cfg.ManagedByAri = false;
            SaveConfig(cfg);
            Shared.LlamaCpp = cfg;
            Shared.Logger.LogInformation("Found existing llama-server: {Path} (v{Version})", existing, cfg.InstalledVersion ?? "unknown");
            _ = CheckForUpdate(cfg);
            return;
        }

        // 3. Download prebuilt release
        Shared.Logger.LogInformation("llama-server not found. Downloading...");
        string installDir = cfg.InstallPath is { Length: > 0 } ? cfg.InstallPath : DefaultInstallPath();
        string server = await DownloadPrebuilt(installDir);
        Shared.LlamaServer = server;
        cfg.InstallPath = installDir;
        cfg.InstalledVersion = await GetVersion(server);
        cfg.ManagedByAri = true;
        SaveConfig(cfg);
        Shared.LlamaCpp = cfg;
    }

    /// <summary>Updates llama.cpp to the latest release.</summary>
    public static async Task<string?> UpdateLlamaCpp()
    {
        LlamaCppStatus cfg = LoadConfig();
        string installDir = cfg.InstallPath is { Length: > 0 } ? cfg.InstallPath : DefaultInstallPath();

        Shared.Logger.LogInformation("Updating llama.cpp...");
        string server = await DownloadPrebuilt(installDir);
        Shared.LlamaServer = server;
        cfg.InstallPath = installDir;
        cfg.InstalledVersion = await GetVersion(server);
        cfg.LatestVersion = cfg.InstalledVersion;
        cfg.ManagedByAri = true;
        cfg.UpdateAvailable = false;
        SaveConfig(cfg);
        Shared.LlamaCpp = cfg;
        Shared.Logger.LogInformation("llama.cpp updated to {Version}", cfg.InstalledVersion);
        return cfg.InstalledVersion;
    }

    /// <summary>Sets a custom install directory.</summary>
    public static void SetLlamaCppPath(string path)
    {
        LlamaCppStatus cfg = LoadConfig();
        cfg.InstallPath = path;
        SaveConfig(cfg);
        Shared.LlamaCpp = cfg;
    }

    /// <summary>Suppresses future update prompts.</summary>
    public static void SuppressLlamaCppUpdates()
    {
        LlamaCppStatus cfg = LoadConfig();
        cfg.SuppressUpdatePrompt = true;
        SaveConfig(cfg);
        Shared.LlamaCpp = cfg;
    }

    /// <summary>Re-enables update prompts.</summary>
    public static void EnableLlamaCppUpdates()
    {
        LlamaCppStatus cfg = LoadConfig();
        cfg.SuppressUpdatePrompt = false;
        SaveConfig(cfg);
        Shared.LlamaCpp = cfg;
    }

    /// <summary>OS-appropriate default install path where other tools can also find it.</summary>
    public static string DefaultInstallPath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "llama.cpp");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "llama.cpp");
    }

    // ── Detection ────────────────────────────────────────────────────────────

    private static async Task<string?> DetectExisting()
    {
        // Homebrew bins (Finder-launched apps miss these on PATH)
        string? brew = FindInBrewBins("llama-server");
        if (brew is not null) return brew;

        // PATH
        string? onPath = await ResolveCommandPath("llama-server");
        if (onPath is not null) return onPath;

        // Common locations
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] common = OperatingSystem.IsWindows()
            ? [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "llama.cpp"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "llama.cpp"),
              ]
            : [
                Path.Combine(home, "llama.cpp"),
                "/usr/local/bin",
                "/opt/llama.cpp",
              ];

        foreach (string dir in common)
        {
            string? found = FindLlamaServerIn(dir);
            if (found is not null) return found;
        }

        // ARI's old managed location
        return FindLlamaServerIn(Paths.ServerDir(Path.Combine("tools", "llama.cpp")));
    }

    private static string? FindInBrewBins(string name)
    {
        foreach (string dir in BrewPaths)
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string? FindLlamaServerIn(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        string name = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
        return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string ResolveInstallDir(string serverPath)
    {
        string dir = Path.GetDirectoryName(serverPath)!;
        if (Path.GetFileName(dir).Equals("bin", StringComparison.OrdinalIgnoreCase))
            dir = Path.GetDirectoryName(dir)!;
        return dir;
    }

    // ── Version ──────────────────────────────────────────────────────────────

    private static async Task<string?> GetVersion(string serverPath)
    {
        try
        {
            Process p = Process.Start(new ProcessStartInfo(serverPath, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            string output = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            return output.Length > 0 ? output.Split('\n')[0].Trim() : null;
        }
        catch { return null; }
    }

    private static async Task CheckForUpdate(LlamaCppStatus cfg)
    {
        if (cfg.SuppressUpdatePrompt) return;
        try
        {
            using HttpClient hc = new() { Timeout = TimeSpan.FromSeconds(10) };
            hc.DefaultRequestHeaders.UserAgent.ParseAdd("ARI-Server/1.0");
            string json = await hc.GetStringAsync("https://api.github.com/repos/ggml-org/llama.cpp/releases/latest");
            using JsonDocument doc = JsonDocument.Parse(json);
            string latestTag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";

            cfg.LatestVersion = latestTag;
            cfg.UpdateAvailable = !string.IsNullOrEmpty(cfg.InstalledVersion)
                && !cfg.InstalledVersion.Contains(latestTag);
            SaveConfig(cfg);
            Shared.LlamaCpp = cfg;

            if (cfg.UpdateAvailable)
                Shared.Logger.LogInformation("llama.cpp update available: {Latest} (installed: {Current})", latestTag, cfg.InstalledVersion);
        }
        catch (Exception ex)
        {
            Shared.Logger.LogDebug("Failed to check for llama.cpp updates: {Error}", ex.Message);
        }
    }

    // ── Download ─────────────────────────────────────────────────────────────

    private static async Task<string> DownloadPrebuilt(string installDir)
    {
        Directory.CreateDirectory(installDir);

        using HttpClient hc = new();
        hc.DefaultRequestHeaders.UserAgent.ParseAdd("ARI-Server/1.0");

        Shared.Logger.LogInformation("Fetching latest llama.cpp release...");
        string json = await hc.GetStringAsync("https://api.github.com/repos/ggml-org/llama.cpp/releases/latest");
        using JsonDocument doc = JsonDocument.Parse(json);

        var (url, isTarGz) = SelectAsset(doc.RootElement.GetProperty("assets"));
        if (url is null)
            throw new Exception(
                "No prebuilt llama.cpp binary matched this platform. " +
                "Please install llama.cpp manually and ensure 'llama-server' is on your PATH.");

        string archivePath = Path.Combine(installDir, isTarGz ? "llama.tar.gz" : "llama.zip");
        Shared.Logger.LogInformation("Downloading llama.cpp: {Url}", url);
        await using (Stream s = await hc.GetStreamAsync(url))
        await using (FileStream fs = File.Create(archivePath))
            await s.CopyToAsync(fs);

        Shared.Logger.LogInformation("Extracting llama.cpp to {Dir}...", installDir);

        if (isTarGz)
        {
            Process tar = Process.Start(new ProcessStartInfo("tar", $"xzf \"{archivePath}\" -C \"{installDir}\" --strip-components=1")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            })!;
            await tar.WaitForExitAsync();
        }
        else
        {
            ZipFile.ExtractToDirectory(archivePath, installDir, overwriteFiles: true);
            string[] nested = Directory.GetDirectories(installDir, "llama-*");
            if (nested.Length == 1)
            {
                foreach (string file in Directory.GetFiles(nested[0], "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(nested[0], file);
                    string dest = Path.Combine(installDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Move(file, dest, overwrite: true);
                }
                Directory.Delete(nested[0], recursive: true);
            }
        }

        File.Delete(archivePath);

        string exe = FindLlamaServerIn(installDir)
            ?? throw new Exception("llama-server not found inside the downloaded llama.cpp archive.");

        if (!OperatingSystem.IsWindows())
        {
            foreach (string bin in Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, "llama-*"))
            {
                Process chmod = Process.Start(new ProcessStartInfo("chmod", $"+x \"{bin}\"") { UseShellExecute = false })!;
                await chmod.WaitForExitAsync();
            }
        }

        Shared.Logger.LogInformation("llama-server ready: {Path}", exe);
        return exe;
    }

    /// <summary>
    /// Selects the right prebuilt binary for this platform.
    /// macOS → Metal (built-in), Windows/Linux → Vulkan (works on Nvidia + AMD).
    /// </summary>
    private static (string? Url, bool IsTarGz) SelectAsset(JsonElement assets)
    {
        string os   = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "macos" : "ubuntu";
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

        List<(string Name, string Url)> candidates = new();
        foreach (JsonElement a in assets.EnumerateArray())
        {
            string name = (a.GetProperty("name").GetString() ?? "").ToLowerInvariant();
            string url  = a.GetProperty("browser_download_url").GetString() ?? "";
            if ((name.EndsWith(".zip") || name.EndsWith(".tar.gz"))
                && name.Contains($"-{os}-") && name.Contains(arch)
                && !name.Contains("xcframework") && !name.Contains("cudart"))
                candidates.Add((name, url));
        }

        if (OperatingSystem.IsMacOS())
        {
            var match = candidates.FirstOrDefault();
            return match.Url is not null ? (match.Url, match.Name.EndsWith(".tar.gz")) : (null, false);
        }

        // Windows + Linux: Vulkan works on both Nvidia and AMD
        {
            var match = candidates.FirstOrDefault(c => c.Name.Contains("vulkan"));
            if (match.Url is not null)
                return (match.Url, match.Name.EndsWith(".tar.gz"));
        }

        return (null, false);
    }

    // ── Config persistence ───────────────────────────────────────────────────

    private static LlamaCppStatus LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<LlamaCppStatus>(File.ReadAllText(ConfigPath), JsonOpts) ?? new();
        }
        catch { }
        return new();
    }

    private static void SaveConfig(LlamaCppStatus cfg)
    {
        try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts)); }
        catch (Exception ex) { Shared.Logger.LogWarning("Failed to save llama.cpp config: {Error}", ex.Message); }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Utilities ────────────────────────────────────────────────────────────

    private static void EnsureBrewInPath()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string current = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in BrewPaths)
            if (!current.Contains(dir))
                Environment.SetEnvironmentVariable("PATH", $"{dir}:{current}");
    }

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
                return outp.Split('\n')[0].Trim();
            return null;
        }
        catch { return null; }
    }
}

using ARI.Common;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace ARI.ImageGen;

public static class Dependency
{
    private const string COMFYUI_REPO    = "https://github.com/comfyanonymous/ComfyUI";
    private const string COMFYUI_ARCHIVE = "https://github.com/comfyanonymous/ComfyUI/archive/refs/heads/master.zip";
    private const string MARKER_FILE     = ".ari-installed";

    // null = check in progress, "" = ready, non-empty = unavailable with reason
    public static string? Status { get; private set; } = null;
    public static string? ComfyUiPath { get; private set; } = null;

    public static async Task Check(string configuredPath)
    {
        string installDir = string.IsNullOrEmpty(configuredPath)
            ? DefaultInstallPath()
            : configuredPath;

        ComfyUiPath = installDir;

        if (File.Exists(Path.Combine(installDir, MARKER_FILE)))
        {
            Shared.Logger.LogInformation("[ImageGen] ComfyUI already provisioned at {Path}.", installDir);
            Status = "";
            return;
        }

        string? python = await FindPython();
        if (python is null)
        {
            string reason = "Python 3 is required for ComfyUI but was not found.";
            Shared.Logger.LogWarning("[ImageGen] {Reason}", reason);
            Status = reason;
            return;
        }

        string? git = await FindGit();

        Shared.Logger.LogInformation("[ImageGen] Installing ComfyUI to {Path}...", installDir);
        Directory.CreateDirectory(installDir);

        try
        {
            if (git is not null)
                await CloneWithGit(git, installDir);
            else
                await DownloadZip(installDir);

            await InstallRequirements(python, installDir);

            File.WriteAllText(Path.Combine(installDir, MARKER_FILE), "1");
            Shared.Logger.LogInformation("[ImageGen] ComfyUI ready.");
            Status = "";
        }
        catch (Exception ex)
        {
            string reason = $"ComfyUI installation failed: {ex.Message}";
            Shared.Logger.LogWarning("[ImageGen] {Reason}", reason);
            Status = reason;
        }
    }

    private static async Task CloneWithGit(string git, string installDir)
    {
        // Clone into a temp name then move contents so installDir itself is the repo root.
        string tempDir = installDir + "_clone_tmp";
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);

        await Run(git, $"clone --depth 1 {COMFYUI_REPO} \"{tempDir}\"");

        MergeIntoInstallDir(tempDir, installDir);
        Directory.Delete(tempDir, recursive: true);
    }

    private static async Task DownloadZip(string installDir)
    {
        string zipPath = Path.Combine(Path.GetTempPath(), "comfyui-master.zip");

        using HttpClient hc = new() { Timeout = TimeSpan.FromMinutes(5) };
        hc.DefaultRequestHeaders.UserAgent.ParseAdd("ARI-Server/1.0");

        Shared.Logger.LogInformation("[ImageGen] Downloading ComfyUI archive...");
        await using (Stream s = await hc.GetStreamAsync(COMFYUI_ARCHIVE))
        await using (FileStream fs = File.Create(zipPath))
            await s.CopyToAsync(fs);

        Shared.Logger.LogInformation("[ImageGen] Extracting ComfyUI...");
        string extractTemp = Path.Combine(Path.GetTempPath(), "comfyui-extract");
        if (Directory.Exists(extractTemp)) Directory.Delete(extractTemp, recursive: true);

        ZipFile.ExtractToDirectory(zipPath, extractTemp);
        File.Delete(zipPath);

        // The zip contains a single top-level folder (ComfyUI-master); move its contents.
        string[] roots = Directory.GetDirectories(extractTemp);
        string source  = roots.Length == 1 ? roots[0] : extractTemp;

        MergeIntoInstallDir(source, installDir);
        Directory.Delete(extractTemp, recursive: true);
    }

    // Move entries from source into dest. Directories that already exist in dest are skipped
    // (preserving any user content like pre-downloaded model files); files are overwritten.
    private static void MergeIntoInstallDir(string source, string dest)
    {
        foreach (string entry in Directory.GetFileSystemEntries(source))
        {
            string name    = Path.GetFileName(entry);
            string destPath = Path.Combine(dest, name);
            if (Directory.Exists(entry))
            {
                if (!Directory.Exists(destPath))
                    Directory.Move(entry, destPath);
                // else: directory already exists (e.g. models/) — leave it alone
            }
            else
            {
                File.Move(entry, destPath, overwrite: true);
            }
        }
    }

    private static async Task InstallRequirements(string python, string installDir)
    {
        string req = Path.Combine(installDir, "requirements.txt");
        if (!File.Exists(req)) return;

        // Create a venv so ComfyUI's dependencies are isolated from the system Python.
        string venvDir = Path.Combine(installDir, "venv");
        if (!Directory.Exists(venvDir))
        {
            Shared.Logger.LogInformation("[ImageGen] Creating Python venv...");
            await Run(python, $"-m venv \"{venvDir}\"");
        }

        string venvPip = VenvExecutable(venvDir, "pip");
        Shared.Logger.LogInformation("[ImageGen] Installing Python requirements into venv...");
        await Run(venvPip, $"install -r \"{req}\" --quiet");
    }

    // Returns the path to the venv's Python executable (used by ImageGenModule to launch ComfyUI).
    public static string? VenvPython(string installDir)
    {
        string venvPython = VenvExecutable(Path.Combine(installDir, "venv"), "python");
        return File.Exists(venvPython) ? venvPython : null;
    }

    private static string VenvExecutable(string venvDir, string name)
    {
        return OperatingSystem.IsWindows()
            ? Path.Combine(venvDir, "Scripts", name + ".exe")
            : Path.Combine(venvDir, "bin", name);
    }

    private static async Task<string?> FindPython()
    {
        // Prefer newer Python versions — ComfyUI requires 3.10+ for some packages.
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "python3.12", "python3.11", "python3.10", "python3", "python" }
            : new[] { "python3.12", "python3.11", "python3.10",
                      "/opt/homebrew/bin/python3.12", "/opt/homebrew/bin/python3.11", "/opt/homebrew/bin/python3.10",
                      "python3", "python" };

        foreach (string candidate in candidates)
        {
            try
            {
                Process p = Process.Start(new ProcessStartInfo(candidate, "--version")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                })!;
                await p.WaitForExitAsync();
                if (p.ExitCode == 0) return candidate;
            }
            catch { }
        }
        return null;
    }

    private static async Task<string?> FindGit()
    {
        try
        {
            Process p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            })!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? "git" : null;
        }
        catch { return null; }
    }

    private static async Task Run(string exe, string args)
    {
        Process p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        })!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            string err = (await p.StandardError.ReadToEndAsync()).Trim();
            throw new Exception($"{exe} {args[..Math.Min(40, args.Length)]}... failed: {err}");
        }
    }

    public static string DefaultInstallPath()
    {
        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "comfyui")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "comfyui");
    }
}

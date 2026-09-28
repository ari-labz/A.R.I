using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace ARI.Common;

/// <summary>
/// Provisions GitHub's own CLI (gh) into ARI's tools folder — nothing is installed system-wide and the user
/// never installs it by hand. Pinned to one release and checksum-verified. Fetched when a GitHub account is
/// connected (and at startup if one already is); the github tool only appears once it's here.
/// </summary>
public static class GhCli
{
    public const string Version = "2.101.0";

    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    private static string VersionDir => Path.Combine(Paths.ServerDir("tools/gh"), Version);

    /// <summary>gh's own config dir — kept inside ARI's folder so it never reads or writes the user's ~/.config/gh.</summary>
    public static string ConfigDir => Paths.ServerDir("tools/gh/config");

    /// <summary>The installed gh executable, or null if it hasn't been fetched yet.</summary>
    public static string? ExecutablePath
    {
        get
        {
            if (!Directory.Exists(VersionDir)) return null;
            string exe = OperatingSystem.IsWindows() ? "gh.exe" : "gh";
            return Directory.EnumerateFiles(VersionDir, exe, SearchOption.AllDirectories).FirstOrDefault();
        }
    }

    /// <summary>Downloads, verifies and unpacks gh if it isn't already present. Safe to call repeatedly and
    /// concurrently. Returns the executable path, or null if it couldn't be installed (logged).</summary>
    public static async Task<string?> EnsureInstalledAsync()
    {
        if (ExecutablePath is { } existing) return existing;

        await InstallLock.WaitAsync();
        try
        {
            if (ExecutablePath is { } raced) return raced;

            (string asset, bool isZip) = AssetName();
            string baseUrl = $"https://github.com/cli/cli/releases/download/v{Version}";
            using HttpClient hc = new();
            hc.DefaultRequestHeaders.UserAgent.ParseAdd("ARI-Server/1.0");

            Shared.Logger.LogInformation("Downloading gh {Version}: {Asset}", Version, asset);
            byte[] archive  = await hc.GetByteArrayAsync($"{baseUrl}/{asset}");
            string sums     = await hc.GetStringAsync($"{baseUrl}/gh_{Version}_checksums.txt");
            string expected = sums.Split('\n')
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(p => p.Length == 2 && p[1] == asset)?[0]
                ?? throw new Exception($"No checksum listed for {asset}.");
            string actual = Convert.ToHexStringLower(SHA256.HashData(archive));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Checksum mismatch for {asset} — refusing to install it.");

            Directory.CreateDirectory(VersionDir);
            string archivePath = Path.Combine(VersionDir, asset);
            await File.WriteAllBytesAsync(archivePath, archive);
            if (isZip)
                ZipFile.ExtractToDirectory(archivePath, VersionDir, overwriteFiles: true);
            else
            {
                using Process tar = Process.Start(new ProcessStartInfo("tar", ["xzf", archivePath, "-C", VersionDir]) { UseShellExecute = false })!;
                await tar.WaitForExitAsync();
            }
            File.Delete(archivePath);

            string exe = ExecutablePath ?? throw new Exception("gh executable not found in the downloaded archive.");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute);
            Shared.Logger.LogInformation("gh ready: {Path}", exe);
            return exe;
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("Could not install gh: {Err}", ex.Message);
            return null;
        }
        finally { InstallLock.Release(); }
    }

    /// <summary>The release asset for this OS/architecture — names as published on cli/cli's release page.</summary>
    private static (string Asset, bool IsZip) AssetName()
    {
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
        if (OperatingSystem.IsMacOS())   return ($"gh_{Version}_macOS_{arch}.zip", true);
        if (OperatingSystem.IsWindows()) return ($"gh_{Version}_windows_{arch}.zip", true);
        return ($"gh_{Version}_linux_{arch}.tar.gz", false);
    }
}

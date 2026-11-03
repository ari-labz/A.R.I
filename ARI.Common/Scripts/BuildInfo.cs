using System.Runtime.InteropServices;
using System.Text.Json;

namespace ARI.Common;

/// <summary>
/// Which ARI is running and on what: the version from manifest.json (the release workflow writes the tag into it), Docker or
/// native, the OS and CPU, and the .NET runtime. <see cref="Summary"/> is the first line of every log, so a pasted log says
/// where it came from.
/// </summary>
public static class BuildInfo
{
    public static string Version { get; } = ReadVersion();

    /// <summary>Set by the official .NET container images, which ARI's Docker image is built on.</summary>
    public static bool InDocker { get; } = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

    /// <summary>For example "A·R·I 0.9.4 — Docker (linux-arm64, Ubuntu 24.04), .NET 10.0.1".</summary>
    public static string Summary =>
        $"A·R·I {Version} — {(InDocker ? "Docker" : "native")} ({RuntimeInformation.RuntimeIdentifier}, {OsName()}), .NET {Environment.Version}";

    private static string ReadVersion()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "manifest.json");
            if (File.Exists(path))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("version", out JsonElement v) && v.GetString() is { Length: > 0 } s)
                    return s;
            }
        }
        catch { /* fall through */ }
        return "0.0.0";
    }

    // On Linux the kernel string says little ("6.8.0-45-generic"); the distro's own name is more use.
    private static string OsName()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                foreach (string line in File.ReadLines("/etc/os-release"))
                    if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                        return line["PRETTY_NAME=".Length..].Trim('"');
            }
            catch { /* fall through */ }
        }
        if (OperatingSystem.IsMacOS()) return $"macOS {Environment.OSVersion.Version}";
        return RuntimeInformation.OSDescription.Trim();
    }
}

namespace ARI.Common;

/// <summary>Finds the Python interpreter the voice and listener setup services build their venvs from.
/// ARI_PYTHON wins; otherwise Homebrew's 3.11 on a Mac (what installs have always used), then the newest
/// supported python3.x on PATH. Linux and containers have no Homebrew, so a fixed path can't be assumed.</summary>
public static class PythonLocator
{
    private static string? cached;

    public static string Find() => cached ??= Resolve();

    private static string Resolve()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("ARI_PYTHON");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
        if (OperatingSystem.IsWindows()) return "python";

        foreach (string brew in new[] { "/opt/homebrew/bin/python3.11", "/usr/local/bin/python3.11" })
            if (File.Exists(brew)) return brew;

        foreach (string name in new[] { "python3.12", "python3.11", "python3.10", "python3" })
            if (OnPath(name) is { } found) return found;

        return "python3";
    }

    private static string? OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);
}

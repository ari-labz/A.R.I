namespace ARI.Common;

/// <summary>A setup step that failed, optionally carrying a user-facing hint about the missing
/// dependency and how to install it.</summary>
public class SetupException : Exception
{
    public string? Hint { get; }
    public SetupException(string message, string? hint = null) : base(message) => Hint = hint;
}

public static class SetupDiagnostics
{
    /// <summary>User-facing fix line for a missing MSVC toolchain — the dependency several Python
    /// packages (such as monotonic_align) compile against on Windows.</summary>
    public const string MsvcBuildToolsHint =
        "Missing: Microsoft C++ Build Tools (needed to compile a package such as monotonic_align). " +
        "Install \"Desktop development with C++\" from https://visualstudio.microsoft.com/visual-cpp-build-tools/, then reinstall.";

    /// <summary>Maps known dependency-failure signatures in captured setup output to a
    /// "what's missing and how to fix it" line. Returns null when nothing recognizable matched.</summary>
    public static string? Diagnose(string output)
    {
        if (string.IsNullOrEmpty(output)) return null;

        bool Has(string s) => output.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("Microsoft Visual C++") || Has("C++ Build Tools") || Has("vcvarsall"))
            return MsvcBuildToolsHint;

        if (Has("WinError 206") || Has("filename or extension is too long"))
            return "Missing setting: Windows long-path support. Enable it (registry " +
                   "HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem\\LongPathsEnabled = 1, or Group Policy " +
                   "\"Enable Win32 long paths\"), then reinstall.";

        if (Has("No matching distribution found for torch") || Has("satisfies the requirement torch"))
            return "No compatible PyTorch wheel for this Python version. Install Python 3.11 or 3.12 (python.org) and reinstall.";

        if (Has("espeak"))
            return "Missing: espeak-ng. Restart ARI to trigger auto-install, or install manually: " +
                   "https://github.com/espeak-ng/espeak-ng/releases";

        return null;
    }
}

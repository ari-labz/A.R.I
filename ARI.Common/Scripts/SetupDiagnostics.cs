using System.Diagnostics;

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
    /// packages (webrtcvad, monotonic_align) compile against on Windows.</summary>
    public const string MsvcBuildToolsHint =
        "Missing: Microsoft C++ Build Tools (needed to compile a package such as webrtcvad). " +
        "Install \"Desktop development with C++\" from https://visualstudio.microsoft.com/visual-cpp-build-tools/, then reinstall.";

    /// <summary>True on Windows when no MSVC toolchain is installed, so a setup step that compiles a
    /// native extension is guaranteed to fail — callers should skip the step rather than wait for it.
    /// Always false off Windows (mac/Linux use the platform compiler).</summary>
    public static bool WindowsMissingMsvcBuildTools()
    {
        if (!OperatingSystem.IsWindows()) return false;

        // Ask vswhere whether the VC compiler component is installed for any VS/Build Tools instance.
        string vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        try
        {
            if (File.Exists(vswhere))
            {
                using Process? p = Process.Start(new ProcessStartInfo(vswhere,
                    "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                });
                if (p is not null)
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    if (!string.IsNullOrWhiteSpace(outp)) return false; // toolchain present
                }
            }

            // Fallback: cl.exe resolvable on PATH (e.g. a Developer Command Prompt environment).
            using Process? where = Process.Start(new ProcessStartInfo("where", "cl.exe")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            });
            if (where is not null)
            {
                where.WaitForExit(5000);
                if (where.HasExited && where.ExitCode == 0) return false;
            }
        }
        catch { /* treat probe failure as "unknown" → missing, so we skip and hint */ }

        return true;
    }

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
            return "Missing: espeak-ng. Install it — macOS: 'brew install espeak-ng'; " +
                   "Windows: https://github.com/espeak-ng/espeak-ng/releases; Linux: 'apt install espeak-ng'.";

        return null;
    }
}

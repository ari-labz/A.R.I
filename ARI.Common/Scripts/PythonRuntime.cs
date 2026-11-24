using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ARI.Common;

/// <summary>
/// Python interpreters ARI downloads and owns, so nothing depends on what the machine happens to have. A clean Mac only has
/// Apple's /usr/bin/python3 stub, which opens an "install developer tools" dialog instead of running, and Windows only has the
/// Store alias, so neither is ever used.
///
/// <see cref="Default"/> is ARI's own Python: some recent version, never a promised one. Use it only for code that runs on
/// any Python 3 (run_code, the documents venv). A module or plugin that needs a particular version calls
/// <see cref="Ensure"/> with that version and builds its own venv from it, so it never relies on which version ARI has.
///
/// Builds come from python-build-standalone (relocatable CPython, the same builds uv uses) and are kept in
/// tools/python/&lt;major.minor&gt;. ARI_PYTHON overrides <see cref="Default"/> only.
/// </summary>
public static class PythonRuntime
{
    private const string RELEASES_API = "https://api.github.com/repos/astral-sh/python-build-standalone/releases/latest";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();
    private static readonly HttpClient http = CreateClient();

    private static string Root => Path.Combine(Paths.Tools, "python");
    private static string DefaultMarker => Path.Combine(Root, "default.txt");

    /// <summary>ARI's own interpreter. Downloads a recent stable Python the first time; after that the same one is kept,
    /// so venvs built from it stay valid. Callers must not depend on its version.</summary>
    public static async Task<string> Default(CancellationToken ct = default)
    {
        string? fromEnv = Environment.GetEnvironmentVariable("ARI_PYTHON");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        if (File.Exists(DefaultMarker) && Installed(File.ReadAllText(DefaultMarker).Trim()) is { } kept) return kept;

        SemaphoreSlim gate = locks.GetOrAdd("default", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(DefaultMarker) && Installed(File.ReadAllText(DefaultMarker).Trim()) is { } raced) return raced;

            (string minor, string url) = PickAsset(await LatestAssets(ct), wantMinor: null);
            string exe = await Install(minor, url, ct);
            Directory.CreateDirectory(Root);
            await File.WriteAllTextAsync(DefaultMarker, minor, ct);
            return exe;
        }
        finally { gate.Release(); }
    }

    /// <summary>An interpreter of exactly this version ("3.11"), downloaded the first time it's asked for. For modules and
    /// plugins with version requirements: build your own venv from the path this returns.</summary>
    public static async Task<string> Ensure(string minorVersion, CancellationToken ct = default)
    {
        string minor = NormaliseMinor(minorVersion);
        if (Installed(minor) is { } exe) return exe;

        SemaphoreSlim gate = locks.GetOrAdd(minor, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Installed(minor) is { } raced) return raced;
            (_, string url) = PickAsset(await LatestAssets(ct), wantMinor: minor);
            return await Install(minor, url, ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>The version a folder asks for in its .python-version file (the pyenv/uv convention), or
    /// <paramref name="fallback"/> when it has none. Voice modules declare theirs this way.</summary>
    public static string DeclaredVersion(string dir, string fallback)
    {
        string file = Path.Combine(dir, ".python-version");
        string? line = File.Exists(file) ? File.ReadLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) : null;
        return line is not null && Regex.IsMatch(line, @"^\d+\.\d+") ? NormaliseMinor(line) : fallback;
    }

    /// <summary>The interpreter inside a venv folder.</summary>
    public static string VenvPython(string venvDir) =>
        Path.Combine(venvDir, OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python");

    /// <summary>Creates a venv from <paramref name="python"/> unless one is already there; returns the venv's interpreter.
    /// A venv left behind by a Python that no longer runs (Apple's stub, a removed Homebrew) is rebuilt.</summary>
    public static async Task<string> EnsureVenv(string venvDir, string python, CancellationToken ct = default)
    {
        string venvPy = VenvPython(venvDir);
        if (File.Exists(venvPy) && await Runs(venvPy, ct)) return venvPy;
        if (Directory.Exists(venvDir)) Directory.Delete(venvDir, recursive: true);

        Directory.CreateDirectory(Path.GetDirectoryName(venvDir)!);
        (int code, string output) = await RunCaptured(python, ["-m", "venv", venvDir], ct);
        if (code != 0 || !File.Exists(venvPy))
            throw new InvalidOperationException($"Couldn't create a Python venv at {venvDir}: {output.Trim()}");
        return venvPy;
    }

    // ── Install ──────────────────────────────────────────────────────────────

    private static string? Installed(string minor)
    {
        if (minor.Length == 0) return null;
        string exe = ExeIn(Path.Combine(Root, minor));
        return File.Exists(exe) ? exe : null;
    }

    private static string ExeIn(string dir) =>
        Path.Combine(dir, OperatingSystem.IsWindows() ? "python.exe" : "bin/python3");

    private static async Task<string> Install(string minor, string url, CancellationToken ct)
    {
        string target  = Path.Combine(Root, minor);
        string staging = Path.Combine(Root, $".staging-{minor}-{Guid.NewGuid():N}");
        string archive = staging + ".tar.gz";
        Directory.CreateDirectory(staging);
        Shared.Logger.LogInformation("Downloading Python {Version} for ARI: {Url}", minor, url);
        try
        {
            await using (Stream s = await http.GetStreamAsync(url, ct))
            await using (FileStream fs = File.Create(archive))
                await s.CopyToAsync(fs, ct);

            (int code, string output) = await RunCaptured("tar", ["-xzf", archive, "-C", staging], ct);
            if (code != 0) throw new InvalidOperationException($"Couldn't unpack Python {minor}: {output.Trim()}");

            // The archive holds a single python/ folder; that folder becomes tools/python/<minor>.
            string unpacked = Path.Combine(staging, "python");
            if (!File.Exists(ExeIn(unpacked))) throw new InvalidOperationException($"The Python {minor} download has no interpreter in it.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(unpacked, target);
        }
        finally
        {
            try { File.Delete(archive); } catch { }
            try { Directory.Delete(staging, recursive: true); } catch { }
        }

        string exe = ExeIn(target);
        if (!await Runs(exe, ct)) throw new InvalidOperationException($"Python {minor} was downloaded but doesn't run on this machine.");
        Shared.Logger.LogInformation("Python {Version} ready: {Path}", minor, exe);
        return exe;
    }

    // ── Picking a build ──────────────────────────────────────────────────────

    private static async Task<List<(string Name, string Url)>> LatestAssets(CancellationToken ct)
    {
        using JsonDocument doc = JsonDocument.Parse(await http.GetStringAsync(RELEASES_API, ct));
        return doc.RootElement.GetProperty("assets").EnumerateArray()
            .Select(a => (a.GetProperty("name").GetString() ?? "", a.GetProperty("browser_download_url").GetString() ?? ""))
            .ToList();
    }

    /// <summary>The install_only build for this machine: of <paramref name="wantMinor"/> if given, otherwise the stable
    /// version one behind the newest in the release. Free-threaded and pre-release builds are never picked.</summary>
    internal static (string Minor, string Url) PickAsset(IEnumerable<(string Name, string Url)> assets, string? wantMinor)
    {
        string triple = PlatformTriple();
        Regex pattern = new($@"^cpython-(\d+)\.(\d+)\.(\d+)\+\d+-{Regex.Escape(triple)}-install_only\.tar\.gz$");

        var builds = assets
            .Select(a => (a.Url, Match: pattern.Match(a.Name)))
            .Where(x => x.Match.Success)
            .Select(x => (x.Url, Major: int.Parse(x.Match.Groups[1].Value), Minor: int.Parse(x.Match.Groups[2].Value), Patch: int.Parse(x.Match.Groups[3].Value)))
            .Where(b => b.Major == 3)
            .ToList();

        if (wantMinor is not null)
            builds = builds.Where(b => $"{b.Major}.{b.Minor}" == wantMinor).ToList();
        else
        {
            // ARI's own Python is one version behind the newest: a brand-new Python often has no prebuilt wheels yet for
            // packages like matplotlib, while the one before it has them all.
            List<int> minors = builds.Select(b => b.Minor).Distinct().OrderByDescending(m => m).ToList();
            if (minors.Count > 1) builds = builds.Where(b => b.Minor == minors[1]).ToList();
        }

        var best = builds.OrderByDescending(b => b.Minor).ThenByDescending(b => b.Patch).FirstOrDefault();
        if (best.Url is null)
            throw new InvalidOperationException(wantMinor is null
                ? $"No Python build is published for this machine ({triple})."
                : $"No Python {wantMinor} build is published for this machine ({triple}).");
        return ($"{best.Major}.{best.Minor}", best.Url);
    }

    internal static string PlatformTriple()
    {
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "aarch64",
            Architecture.X64   => "x86_64",
            Architecture other => throw new PlatformNotSupportedException($"ARI can't fetch Python for {other} processors."),
        };
        if (OperatingSystem.IsMacOS())   return $"{arch}-apple-darwin";
        if (OperatingSystem.IsWindows()) return $"{arch}-pc-windows-msvc";
        return $"{arch}-unknown-linux-gnu";
    }

    internal static string NormaliseMinor(string version)
    {
        Match m = Regex.Match(version.Trim(), @"^(\d+)\.(\d+)");
        if (!m.Success) throw new ArgumentException($"\"{version}\" isn't a Python version like 3.11.", nameof(version));
        return $"{m.Groups[1].Value}.{m.Groups[2].Value}";
    }

    // ── Processes ────────────────────────────────────────────────────────────

    private static async Task<bool> Runs(string exe, CancellationToken ct)
    {
        try { return (await RunCaptured(exe, ["-c", "import sys; sys.exit(0)"], ct)).Code == 0; }
        catch { return false; }
    }

    private static async Task<(int Code, string Output)> RunCaptured(string exe, string[] args, CancellationToken ct)
    {
        ProcessStartInfo psi = new(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {exe}.");
        Task<string> stdout = p.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await stdout + await stderr);
    }

    private static HttpClient CreateClient()
    {
        HttpClient c = new() { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ARI-Server/1.0");
        return c;
    }
}

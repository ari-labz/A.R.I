using ARI.Common;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// Provisions dependencies that are only needed when the LLM module is active.
/// Follows the same pattern as ARI.Core's Dependency.cs — check, detect, install.
/// </summary>
public static class Dependency
{
    private const string SearXngImage     = "searxng/searxng";
    private const string SearXngContainer = "ari-searxng";
    internal const int   SearXngPort      = 8085;

    // ── SearXNG ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures SearXNG is running in Docker. Pulls the image and starts the container if needed.
    /// Logs a warning and returns cleanly if Docker isn't available — web tools will still register
    /// but will return a clear error when called.
    /// </summary>
    public static async Task CheckSearXng()
    {
        string? docker = await FindDocker();
        if (docker is null)
        {
            Shared.Logger.LogWarning(
                "[LLM] Docker not found — SearXNG will not be available. " +
                "Install Docker Desktop to enable web search: https://www.docker.com/products/docker-desktop/");
            return;
        }

        // Ensure the Docker daemon is up — launch Docker Desktop if not.
        if (!await IsDaemonRunning(docker))
        {
            Shared.Logger.LogInformation("[LLM] Docker daemon not running — starting Docker Desktop...");
            bool started = await StartDockerDaemon();
            if (!started)
            {
                Shared.Logger.LogWarning("[LLM] Docker daemon did not start in time — SearXNG will not be available.");
                return;
            }
            Shared.Logger.LogInformation("[LLM] Docker daemon ready.");
        }

        // Already running?
        if (await IsContainerRunning(docker))
        {
            Shared.Logger.LogInformation("[LLM] SearXNG already running on port {Port}.", SearXngPort);
            return;
        }

        // Container exists but is stopped — start it.
        if (await ContainerExists(docker))
        {
            Shared.Logger.LogInformation("[LLM] Starting existing SearXNG container...");
            await RunDocker(docker, $"start {SearXngContainer}");
            Shared.Logger.LogInformation("[LLM] SearXNG started on port {Port}.", SearXngPort);
            return;
        }

        // First run — pull image and create container.
        Shared.Logger.LogInformation("[LLM] Pulling SearXNG image (one-time download)...");
        await RunDocker(docker, $"pull {SearXngImage}");

        string settingsDir = Path.Combine(Paths.PersistentData, "searxng");
        Directory.CreateDirectory(settingsDir);
        WriteSearXngSettings(settingsDir);

        Shared.Logger.LogInformation("[LLM] Creating SearXNG container on port {Port}...", SearXngPort);
        await RunDocker(docker,
            $"run -d --name {SearXngContainer} " +
            $"-p {SearXngPort}:8080 " +
            $"-v \"{settingsDir}:/etc/searxng\" " +
            $"--restart unless-stopped " +
            $"{SearXngImage}");

        Shared.Logger.LogInformation("[LLM] SearXNG ready on port {Port}.", SearXngPort);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<bool> IsDaemonRunning(string docker)
    {
        try
        {
            (int code, _, _) = await RunDockerCaptured(docker, "info");
            return code == 0;
        }
        catch { return false; }
    }

    private static async Task<bool> StartDockerDaemon()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", "-a Docker") { UseShellExecute = false });
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("cmd", "/c start \"\" \"C:\\Program Files\\Docker\\Docker\\Docker Desktop.exe\"") { UseShellExecute = false });
            else
                // Linux: daemon is typically managed by systemd, not a desktop app
                Process.Start(new ProcessStartInfo("sudo", "systemctl start docker") { UseShellExecute = false });
        }
        catch { return false; }

        // Poll until daemon responds or we time out (~60s).
        string? docker = await FindDocker();
        if (docker is null) return false;

        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(2000);
            if (await IsDaemonRunning(docker)) return true;
        }
        return false;
    }

    private static async Task<string?> FindDocker()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["docker", @"C:\Program Files\Docker\Docker\resources\bin\docker.exe"]
            : ["/usr/local/bin/docker", "/usr/bin/docker", "/opt/homebrew/bin/docker", "docker"];

        foreach (string candidate in candidates)
        {
            try
            {
                ProcessStartInfo psi = new(candidate, "--version")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };
                Process p = Process.Start(psi)!;
                await p.WaitForExitAsync();
                if (p.ExitCode == 0) return candidate;
            }
            catch { }
        }
        return null;
    }

    private static async Task<bool> IsContainerRunning(string docker)
    {
        (int code, string output, _) = await RunDockerCaptured(docker,
            $"inspect -f {{{{.State.Running}}}} {SearXngContainer}");
        return code == 0 && output.Trim() == "true";
    }

    private static async Task<bool> ContainerExists(string docker)
    {
        (int code, _, _) = await RunDockerCaptured(docker,
            $"inspect --type container {SearXngContainer}");
        return code == 0;
    }

    private static async Task RunDocker(string docker, string args)
    {
        Process p = Process.Start(new ProcessStartInfo(docker, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        })!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            string err = (await p.StandardError.ReadToEndAsync()).Trim();
            throw new Exception($"docker {args[..Math.Min(40, args.Length)]}... failed: {err}");
        }
    }

    private static async Task<(int Code, string Output, string Error)> RunDockerCaptured(string docker, string args)
    {
        Process p = Process.Start(new ProcessStartInfo(docker, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        })!;
        string output = await p.StandardOutput.ReadToEndAsync();
        string error  = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, output, error);
    }

    /// <summary>
    /// Writes a minimal settings.yml that enables JSON output and sensible engine defaults.
    /// Only written once — if the file already exists it is left alone so the user can customise it.
    /// </summary>
    private static void WriteSearXngSettings(string dir)
    {
        string path = Path.Combine(dir, "settings.yml");
        if (File.Exists(path)) return;

        File.WriteAllText(path, """
            use_default_settings: true

            server:
              secret_key: "ari-searxng-change-me"
              limiter: false
              image_proxy: false

            search:
              formats:
                - html
                - json

            engines:
              - name: bing
                engine: bing
                disabled: false
              - name: duckduckgo
                engine: duckduckgo
                disabled: false
              - name: brave
                engine: brave
                disabled: false
            """);
    }
}

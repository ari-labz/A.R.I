using ARI.Common;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace ARI.LLM;

public static class Dependency
{
    private const string SEARXNG_IMAGE     = "searxng/searxng";
    private const string SEARXNG_CONTAINER = "ari-searxng";
    internal const int   SEARXNG_PORT      = 8085;
    private const int    LLM_SERVER_STARTUP_TIMEOUT_SEC = 60;
    private const int    LLM_STARTUP_POLL_INTERVAL_MS   = 2000;

    // null = check still in progress, "" = ready, non-empty = unavailable with reason
    internal static string? SearXngStatus { get; private set; } = null;

    // ── SearXNG ───────────────────────────────────────────────────────────────

    public static void StartSearXng() => _ = CheckSearXngAsync();

    private static async Task CheckSearXngAsync()
    {
        string? docker = await FindDocker();
        if (docker is null)
        {
            string reason = "Docker is not installed. Install Docker Desktop to enable web search: https://www.docker.com/products/docker-desktop/";
            Shared.Logger.LogWarning("[LLM] Docker not found — SearXNG will not be available. {Reason}", reason);
            SearXngStatus = reason;
            return;
        }

        if (!await IsDaemonRunning(docker))
        {
            Shared.Logger.LogInformation("[LLM] Docker daemon not running — starting Docker Desktop...");
            bool started = await StartDockerDaemon();
            if (!started)
            {
                string reason = OperatingSystem.IsLinux()
                    ? "Docker daemon is not running. Start it with: sudo systemctl start docker  Then restart ARI."
                    : "Docker daemon did not start in time. Please start Docker Desktop and restart ARI.";
                Shared.Logger.LogWarning("[LLM] Docker daemon did not start — SearXNG will not be available.");
                SearXngStatus = reason;
                return;
            }
            Shared.Logger.LogInformation("[LLM] Docker daemon ready.");
        }

        if (await IsContainerRunning(docker))
        {
            Shared.Logger.LogInformation("[LLM] SearXNG already running on port {Port}.", SEARXNG_PORT);
            SearXngStatus = "";
            return;
        }

        if (await ContainerExists(docker))
        {
            Shared.Logger.LogInformation("[LLM] Starting existing SearXNG container...");
            await RunDocker(docker, $"start {SEARXNG_CONTAINER}");
            Shared.Logger.LogInformation("[LLM] SearXNG started on port {Port}.", SEARXNG_PORT);
            SearXngStatus = "";
            return;
        }

        Shared.Logger.LogInformation("[LLM] Pulling SearXNG image (one-time download)...");
        await RunDocker(docker, $"pull {SEARXNG_IMAGE}");

        string settingsDir = Path.Combine(Paths.PersistentData, "searxng");
        Directory.CreateDirectory(settingsDir);
        WriteSearXngSettings(settingsDir);

        Shared.Logger.LogInformation("[LLM] Creating SearXNG container on port {Port}...", SEARXNG_PORT);
        await RunDocker(docker,
            $"run -d --name {SEARXNG_CONTAINER} " +
            $"-p {SEARXNG_PORT}:8080 " +
            $"-v \"{settingsDir}:/etc/searxng\" " +
            $"--restart unless-stopped " +
            $"{SEARXNG_IMAGE}");

        Shared.Logger.LogInformation("[LLM] SearXNG ready on port {Port}.", SEARXNG_PORT);
        SearXngStatus = "";
    }

    public static void StopSearXng() => _ = StopSearXngAsync();

    private static async Task StopSearXngAsync()
    {
        string? docker = await FindDocker();
        if (docker is null) return;
        if (!await IsContainerRunning(docker)) return;
        await RunDocker(docker, $"stop {SEARXNG_CONTAINER}");
        Shared.Logger.LogInformation("[LLM] SearXNG stopped.");
        SearXngStatus = null;
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
            {
                // Linux: cannot start the daemon here without a password prompt; user must start it
                Shared.Logger.LogWarning(
                    "[LLM] Docker daemon is not running. Start it with: sudo systemctl start docker  " +
                    "Then restart ARI.");
                return false;
            }
        }
        catch { return false; }

        // Poll until daemon responds or we time out (~60s).
        string? docker = await FindDocker();
        if (docker is null) return false;

        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(LLM_SERVER_STARTUP_TIMEOUT_SEC))
        {
            await Task.Delay(LLM_STARTUP_POLL_INTERVAL_MS);
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
            catch (Exception ex)
            {
                Shared.Logger.LogDebug("[LLM] Docker candidate {Path} not usable: {Error}", candidate, ex.Message);
            }
        }
        return null;
    }

    private static async Task<bool> IsContainerRunning(string docker)
    {
        (int code, string output, _) = await RunDockerCaptured(docker,
            $"inspect -f {{{{.State.Running}}}} {SEARXNG_CONTAINER}");
        return code == 0 && output.Trim() == "true";
    }

    private static async Task<bool> ContainerExists(string docker)
    {
        (int code, _, _) = await RunDockerCaptured(docker,
            $"inspect --type container {SEARXNG_CONTAINER}");
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

    private const string PLACEHOLDER_SECRET = "ari-searxng-change-me";

    private static void WriteSearXngSettings(string dir)
    {
        string path = Path.Combine(dir, "settings.yml");
        bool needsWrite = !File.Exists(path)
            || File.ReadAllText(path).Contains(PLACEHOLDER_SECRET);

        if (!needsWrite) return;

        string secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(path, $"""
            use_default_settings: true

            server:
              secret_key: "{secret}"
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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace ARI.Common;

public static class Shared
{
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    public static ILogger Logger { get; private set; } = NullLogger.Instance;

    public static string LogPath { get; set; } = "";

    // Global kill switch for memory writes. When DevMode is ON, Engram never runs, so no autonomous
    // run can ever mutate the brain vault. Resolved once at startup via ResolveDevMode.
    //
    // RULE: DevMode is OFF only for the user running in Rider or for an official build. Every other
    // run — in particular any build launched by automation/Claude — MUST run with DevMode ON. That
    // is enforced by the ARI_DEVMODE env var, which can only force DevMode *ON*, never off: an
    // automated launcher sets ARI_DEVMODE=1 and Engram is guaranteed disabled regardless of config.
    public static bool DevMode { get; private set; }

    public static void ResolveDevMode(bool configValue)
    {
        string env = (Environment.GetEnvironmentVariable("ARI_DEVMODE") ?? "").Trim().ToLowerInvariant();
        bool envForcesOn = env is "1" or "true" or "yes" or "on";
        DevMode = envForcesOn || configValue;   // env can only turn it ON, never off
    }

    // Resolved llama-server executable, set by Dependency.CheckLlamaCpp at startup. Defaults to the
    // bare command name (found on PATH); becomes a full path when we download a managed build.
    public static string LlamaServer { get; set; } = "llama-server";

    public static void InitialiseLogger(ILoggerFactory factory, string categoryName = "ARI")
    {
        _factory = factory;
        Logger = factory.CreateLogger(categoryName);
    }

    public static ILogger GetLogger(string categoryName) => _factory.CreateLogger(categoryName);

    public static Process RunCommand(string command, string arguments)
    {
        ProcessStartInfo processInfo = new ProcessStartInfo(command, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        Process process = Process.Start(processInfo)
            ?? throw new Exception($"Failed to start process: {command} {arguments}");

        return process;
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace ARI.Common;

public static class Shared
{
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    public static ILogger Logger { get; private set; } = NullLogger.Instance;

    public static string LogPath { get; set; } = "";

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

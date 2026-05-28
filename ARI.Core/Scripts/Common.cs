using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace ARI.Core.Scripts;

public static class Common
{
    public static ILogger Logger { get; private set; } = NullLogger.Instance;

    public static void InitialiseLogger(ILoggerFactory factory)
    {
        Logger = factory.CreateLogger("ARI.Core");
    }

    public static Process RunCommand(string command, string arguments)
    {
        ProcessStartInfo processInfo = new ProcessStartInfo(command, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        Process process = Process.Start(processInfo);

        if (process == null)
            throw new Exception($"Failed to start process: {command} {arguments}");

        return process;
    }
}
using System.Diagnostics;

namespace ARI.Core.Scripts;

public static class Common
{

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
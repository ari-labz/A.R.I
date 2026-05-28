using System.Diagnostics;

namespace ARI.Core.Scripts;

public class Dependency
{
    /// <summary>
    /// Checks that Docker is intalled
    /// </summary>
    public static async Task CheckDocker()
    {
        try
        {
            Process process = Common.RunCommand("docker", "--version");
            await process.WaitForExitAsync();

            Common.Logger.LogInformation("Docker is installed.");
        }
        catch (Exception e)
        {
            Common.Logger.LogError("Docker is not installed.");
            throw new Exception("Docker is not installed. Please install Docker Desktop from https://docker.com and try again.");
        }
    }


    /// <summary>
    /// Checks that Python is intalled
    /// </summary>
    public static async Task CheckPython()
    {
        try
        {
            Process process = Common.RunCommand("python3", "--version");
            await process.WaitForExitAsync();

            Common.Logger.LogInformation("Python is installed.");
        }
        catch (Exception e)
        {
            Common.Logger.LogError("Python is not installed.");
            throw new Exception("Python is not installed. Please install Python and try again.");
        }
    }
}
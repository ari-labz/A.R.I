using System.Diagnostics;

namespace ARI.Core.Scripts;

public class Dependency
{
    
    public static async Task CheckDocker()
    {
        try
        {
            Process process = Common.RunCommand("docker", "--version");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)

            Console.WriteLine("Docker is installed.");
        }
        catch (Exception e)
        {
            Console.WriteLine("Docker is not installed. Please install Docker Desktop from https://docker.com and try again.");   
            throw new Exception("Docker is not installed. Please install Docker Desktop from https://docker.com and try again.");
        }
    }

    public static async Task CheckPython()
    {
        try
        {
            Process process = Common.RunCommand("python3", "--version");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new Exception("");

            Console.WriteLine("Python is installed.");
        }
        catch (Exception e)
        {
            Console.WriteLine("Python is not installed. Please install Python and try again.");
            throw new Exception("Python is not installed. Please install Python and try again.");
        }
    }
}
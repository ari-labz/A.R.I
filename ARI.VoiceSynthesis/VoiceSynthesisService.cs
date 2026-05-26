using System.Diagnostics;

namespace ARI.VoiceSynthesis;

public class VoiceSynthesisService
{
    private Process? rvcProcess;
    private readonly string rvcPath;

    public VoiceSynthesisService(string rvcPath)
    {
        this.rvcPath = rvcPath;
    }

    public bool IsRunning => rvcProcess != null && !rvcProcess.HasExited;

    public void Start()
    {
        if (IsRunning) return;
        
        if (!IsPythonInstalled())
            throw new InvalidOperationException("Python is not installed or not found on the system PATH.");


        rvcProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "infer-web.py",
                WorkingDirectory = rvcPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        rvcProcess.Start();
    }

    public void Stop()
    {
        if (!IsRunning) return;

        rvcProcess!.Kill();
        rvcProcess = null;
    }
    
    private static bool IsPythonInstalled()
    {
        try
        {
            Process process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
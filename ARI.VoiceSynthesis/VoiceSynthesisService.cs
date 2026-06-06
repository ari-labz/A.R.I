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
                FileName = "/bin/bash",
                Arguments = $"-c \"cd '{rvcPath}' && OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 RVC_FORCE_CPU=1 PYTORCH_ENABLE_MPS_FALLBACK=1 /opt/homebrew/bin/python3.11 infer-web.py --noautoopen\"",
                WorkingDirectory = rvcPath,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
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
                    FileName = "/opt/homebrew/bin/python3.11",
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
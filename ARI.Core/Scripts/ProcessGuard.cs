using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ARI.Core.Scripts;

// Startup guard: before ARI binds its ports or spawns its child servers, kill any OTHER ARI instance
// still running (and the child servers it left behind). Without this, a stale instance — e.g. one
// launched from a terminal — keeps the server/llama/whisper/synthesis ports, and a fresh launch from
// Rider fails to bind.
//
// Matching is deliberately strict: it looks at the process's EXECUTABLE (argv[0]), not anywhere in the
// command line, so a shell that merely mentions the repo path is never a target. The current process
// and all of its ancestors are always excluded, so the guard can never kill its own launcher.
public static class ProcessGuard
{
    public static void KillStaleInstances(ILogger logger)
    {
        int self = Environment.ProcessId;
        try
        {
            List<(int Pid, int Ppid, string Cmd)> procs = ListProcesses();
            HashSet<int> protectedPids = AncestorsOf(self, procs);   // self + everything that launched us

            List<Process> victims = new();
            foreach ((int pid, int _, string cmd) in procs)
            {
                if (protectedPids.Contains(pid) || !IsAriProcess(cmd)) continue;

                logger.LogWarning("[ProcessGuard] Killing stale ARI process {Pid}: {Cmd}", pid, cmd.Length > 120 ? cmd[..120] + "…" : cmd);
                try { Process victim = Process.GetProcessById(pid); victim.Kill(entireProcessTree: true); victims.Add(victim); }
                catch { /* already gone */ }
            }

            // Wait for the killed processes to actually exit (and their ports to free) before this instance
            // binds — otherwise a Rider launch can still hit "address already in use" on the server port.
            foreach (Process v in victims)
            {
                try { v.WaitForExit(3000); } catch { /* ignore */ }
                v.Dispose();
            }

            logger.LogInformation("[ProcessGuard] {Killed} stale ARI process(es) cleared.", victims.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[ProcessGuard] Scan failed ({Error}) — continuing startup.", ex.Message);
        }
    }

    // True only when the process's executable is one of ARI's own — the apphost, the dotnet host running
    // ARI.Core.dll, one of the python servers, or a llama-server serving an ARI model. Matched on argv[0]
    // so an editor/shell/grep that happens to contain "ARI.Core" in its arguments is never a target.
    private static bool IsAriProcess(string cmd)
    {
        string exe = Argv0(cmd);
        string exeName = exe.Contains('/') ? exe[(exe.LastIndexOf('/') + 1)..] : exe;

        if (exeName == "ARI.Core") return true;                                            // the apphost binary
        if (exeName == "dotnet"  && cmd.Contains("ARI.Core.dll", StringComparison.Ordinal)) return true;
        if (exeName.StartsWith("Python", StringComparison.OrdinalIgnoreCase) || exeName is "python" or "python3")
            return cmd.Contains("whisper_serve.py", StringComparison.Ordinal)
                || cmd.Contains("StyleTTS2/serve.py", StringComparison.Ordinal);
        if (exeName == "llama-server")
            return cmd.Contains("/A.R.I/", StringComparison.Ordinal);                       // only ARI's llama, not a generic one
        return false;
    }

    private static string Argv0(string cmd)
    {
        int space = cmd.IndexOf(' ');
        return space < 0 ? cmd : cmd[..space];
    }

    // Walk parent pointers up from `start`, returning it plus every ancestor pid.
    private static HashSet<int> AncestorsOf(int start, List<(int Pid, int Ppid, string Cmd)> procs)
    {
        Dictionary<int, int> parent = procs.ToDictionary(p => p.Pid, p => p.Ppid);
        HashSet<int> chain = new() { start };
        int cur = start;
        while (parent.TryGetValue(cur, out int ppid) && ppid > 0 && chain.Add(ppid))
            cur = ppid;
        return chain;
    }

    private static List<(int Pid, int Ppid, string Cmd)> ListProcesses()
    {
        ProcessStartInfo psi = new() { FileName = "ps", RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-axo");
        psi.ArgumentList.Add("pid=,ppid=,command=");
        using Process ps = Process.Start(psi)!;
        string output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();

        List<(int, int, string)> procs = new();
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimStart();
            int s1 = line.IndexOf(' ');
            if (s1 <= 0 || !int.TryParse(line[..s1], out int pid)) continue;
            string rest = line[(s1 + 1)..].TrimStart();
            int s2 = rest.IndexOf(' ');
            if (s2 <= 0 || !int.TryParse(rest[..s2], out int ppid)) continue;
            string cmd = rest[(s2 + 1)..].TrimStart();
            if (cmd.Length > 0) procs.Add((pid, ppid, cmd));
        }
        return procs;
    }
}

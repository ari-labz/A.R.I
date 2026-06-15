using System.Diagnostics;

namespace ARI.API;

/// <summary>Tracks ARI process RAM including the active llama-server, read via ModelManagerHolder.</summary>
public class SystemInfoHolder
{
    private readonly ModelManagerHolder modelManagerHolder;

    public SystemInfoHolder(ModelManagerHolder modelManagerHolder)
    {
        this.modelManagerHolder = modelManagerHolder;
    }

    public long GetTotalRamBytes()
    {
        long total = Process.GetCurrentProcess().WorkingSet64;
        int pid = modelManagerHolder.ActivePid;
        if (pid > 0)
        {
            try { total += Process.GetProcessById(pid).WorkingSet64; }
            catch { /* process may have exited */ }
        }
        return total;
    }
}

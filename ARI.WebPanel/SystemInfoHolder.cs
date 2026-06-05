using System.Diagnostics;

namespace ARI.WebPanel;

/// <summary>Tracks external process PIDs so the control panel can include them in RAM totals.</summary>
public class SystemInfoHolder
{
    private int llamaPid = -1;

    public void SetLlamaPid(int pid) => llamaPid = pid;

    public long GetTotalRamBytes()
    {
        long total = Process.GetCurrentProcess().WorkingSet64;
        if (llamaPid > 0)
        {
            try { total += Process.GetProcessById(llamaPid).WorkingSet64; }
            catch { /* process may have exited */ }
        }
        return total;
    }
}

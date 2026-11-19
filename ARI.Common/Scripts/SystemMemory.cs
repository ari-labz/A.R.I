namespace ARI.Common;

/// <summary>How much memory this machine has and how much is free right now, in GB. The language-model servers count as
/// used while they are loaded, so "free" is what a generation job could take without stopping them.</summary>
public static class SystemMemory
{
    public static (double TotalGb, double FreeGb) Read()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        double total = info.TotalAvailableMemoryBytes / 1073741824.0;
        double free  = Math.Max(0, (info.TotalAvailableMemoryBytes - info.MemoryLoadBytes) / 1073741824.0);
        return (Math.Round(total, 1), Math.Round(free, 1));
    }

    /// <summary>Memory kept back for the operating system and ARI itself when working out what a job can use.</summary>
    public const double ReservedGb = 6;
}

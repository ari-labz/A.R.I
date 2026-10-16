using System.Diagnostics;

namespace ARI.API.Setup;

public record GpuInfo(string Name, long VramBytes);
public record HardwareReport(long TotalBytes, long UsableBytes, long AvailableBytes, bool Unified, GpuInfo? Gpu, string Note);

/// <summary>
/// What a model has to fit into. A container's memory limit (cgroup) is respected through the .NET GC's
/// view of "total available memory". A discrete NVIDIA GPU with its own VRAM is the real budget for
/// inference; on unified-memory machines (DGX Spark, Apple Silicon) nvidia-smi reports no VRAM figure
/// and the system RAM is what the GPU uses, so that is the budget.
/// </summary>
public static class HardwareInfo
{
    public static HardwareReport Detect()
    {
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long avail = ReadMemAvailable() ?? total;
        avail = Math.Min(avail, total);

        GpuInfo? gpu = QueryNvidia();
        bool unified = gpu is null || gpu.VramBytes <= 0;

        long reserve = Math.Max(2L << 30, total / 10);          // OS, ARI itself, headroom
        long usable;
        string note;
        if (!unified)
        {
            usable = gpu!.VramBytes - (1L << 30);
            note = $"Using the VRAM of {gpu.Name}. Models that spill into system RAM run, but far slower.";
        }
        else
        {
            usable = Math.Max(0, total - reserve);
            note = gpu is null
                ? "No NVIDIA GPU visible — models would run on the CPU from system memory."
                : $"{gpu.Name} shares system memory, so all of it counts (minus a reserve for ARI and the OS).";
        }
        return new HardwareReport(total, usable, avail, unified, gpu, note);
    }

    private static long? ReadMemAvailable()
    {
        try
        {
            if (!File.Exists("/proc/meminfo")) return null;
            foreach (string line in File.ReadLines("/proc/meminfo"))
                if (line.StartsWith("MemAvailable:"))
                    return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
        }
        catch { }
        return null;
    }

    private static GpuInfo? QueryNvidia()
    {
        try
        {
            using Process p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(4000) || p.ExitCode != 0) return null;
            string first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            string[] parts = first.Split(',', 2);
            if (parts.Length < 1 || parts[0].Trim().Length == 0) return null;
            long vram = parts.Length == 2 && long.TryParse(parts[1].Trim(), out long mib) ? mib << 20 : 0;
            return new GpuInfo(parts[0].Trim(), vram);
        }
        catch { return null; }
    }
}

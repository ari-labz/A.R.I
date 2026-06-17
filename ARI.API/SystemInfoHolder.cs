using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ARI.API;

public record RamSegment(string Label, string ServerName, long Bytes);

/// <summary>Tracks ARI process RAM: the .NET host, all active llama-server instances, and F5-TTS.</summary>
public class SystemInfoHolder
{
    private readonly ModelManagerHolder modelManagerHolder;

    public SystemInfoHolder(ModelManagerHolder modelManagerHolder)
    {
        this.modelManagerHolder = modelManagerHolder;
    }

    // Returns total system RAM used (App + Wired + Compressed) — matches Activity Monitor "Used".
    // Per-process phys_footprint misses Metal/GPU wired allocations where model weights and KV caches live.
    public long GetTotalRamBytes()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Process.GetCurrentProcess().WorkingSet64;

        try
        {
            var psi = new ProcessStartInfo("/bin/sh", "-c \"vm_stat\"")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using Process p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            long pageSize = 16384;
            long active = 0, wired = 0, compressed = 0;
            foreach (string line in output.Split('\n'))
            {
                if (line.StartsWith("Mach Virtual Memory Statistics") && line.Contains("page size of "))
                {
                    int s = line.IndexOf("page size of ") + 13;
                    int e = line.IndexOf(" bytes", s);
                    if (e > s && long.TryParse(line[s..e], out long ps)) pageSize = ps;
                }
                static long Pages(string l) { string v = l[(l.LastIndexOf(':') + 1)..].Trim().TrimEnd('.'); return long.TryParse(v, out long n) ? n : 0; }
                if (line.StartsWith("Pages active:"))                active     = Pages(line);
                if (line.StartsWith("Pages wired down:"))            wired      = Pages(line);
                if (line.StartsWith("Pages occupied by compressor:")) compressed = Pages(line);
            }
            return (active + wired + compressed) * pageSize;
        }
        catch { return Process.GetCurrentProcess().WorkingSet64; }
    }

    public List<RamSegment> GetRamBreakdown()
    {
        long totalSystem = GetTotalRamBytes();
        long accounted   = 0;

        // Collect model file sizes and KV weights per server
        var serverEntries = new List<(string ServerName, string Label, long FileBytes, int ContextSize)>();
        foreach (KeyValuePair<string, ServerStatus> kv in modelManagerHolder.Servers)
        {
            ServerStatus server = kv.Value;
            if (server is null || server.ActiveFile is null || server.Pid <= 0) continue;
            ModelInfo? info = modelManagerHolder.AllModels.FirstOrDefault(m =>
                string.Equals(m.File, server.ActiveFile, StringComparison.OrdinalIgnoreCase));
            long fileBytes = info?.FileSizeBytes ?? 0;
            if (fileBytes <= 0) continue;
            serverEntries.Add((kv.Key, server.ActiveName ?? kv.Key, fileBytes, server.ContextSize));
            accounted += fileBytes;
        }

        // F5-TTS — PyTorch uses regular app memory, not Metal wired, so phys_footprint is accurate
        long pythonBytes = 0;
        foreach (Process p in Process.GetProcessesByName("python").Concat(Process.GetProcessesByName("python3")))
        {
            try { pythonBytes += PhysFootprint(p.Id); } catch { }
        }
        accounted += pythonBytes;

        // KV pool = total system - model weights - F5-TTS - OS baseline
        const long OsBaselineBytes = 2_684_354_560L; // ~2.5 GB for macOS kernel + ARI .NET
        long kvPool   = Math.Max(0, totalSystem - accounted - OsBaselineBytes);
        long totalCtx = serverEntries.Sum(e => (long)e.ContextSize);

        // Emit segments: each model immediately followed by its KV cache, then F5-TTS
        var segments = new List<RamSegment>();
        foreach (var e in serverEntries)
        {
            segments.Add(new RamSegment(e.Label, e.ServerName, e.FileBytes));
            if (kvPool > 0 && totalCtx > 0)
            {
                long kvBytes = kvPool * e.ContextSize / totalCtx;
                if (kvBytes > 0) segments.Add(new RamSegment($"{e.ServerName} KV cache", e.ServerName, kvBytes));
            }
        }
        if (kvPool > 0 && totalCtx == 0)
            segments.Add(new RamSegment("KV cache", "System", kvPool));

        if (pythonBytes > 0) segments.Add(new RamSegment("F5-TTS", "TTS", pythonBytes));

        return segments;
    }

    // Returns swap currently used by the OS in MB (macOS only via sysctl).
    public double GetSwapMb()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return 0;
        try
        {
            var psi = new ProcessStartInfo("/bin/sh", "-c \"sysctl vm.swapusage\"")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using Process p = Process.Start(psi)!;
            string line = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            // format: "vm.swapusage: total = 2048.00M  used = 512.00M  free = ..."
            int usedIdx = line.IndexOf("used = ", StringComparison.Ordinal);
            if (usedIdx < 0) return 0;
            string after = line[(usedIdx + 7)..].TrimStart();
            int mIdx = after.IndexOf('M');
            if (mIdx < 0) return 0;
            return double.TryParse(after[..mIdx], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double mb) ? mb : 0;
        }
        catch { return 0; }
    }

    // ── macOS phys_footprint via proc_pid_rusage ─────────────────────────────
    // This captures wired/Metal/GPU memory that WorkingSet64 misses.

    private static long PhysFootprint(int pid)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try { return Process.GetProcessById(pid).WorkingSet64; }
            catch { return 0; }
        }

        try
        {
            var info = new RUsageInfoV0();
            int result = proc_pid_rusage(pid, 0, ref info);
            return result == 0 ? (long)info.ri_phys_footprint : 0;
        }
        catch { return 0; }
    }

    [DllImport("libproc.dylib")]
    private static extern int proc_pid_rusage(int pid, int flavor, ref RUsageInfoV0 info);

    [StructLayout(LayoutKind.Sequential)]
    private struct RUsageInfoV0
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ri_uuid;
        public ulong ri_user_time;
        public ulong ri_system_time;
        public ulong ri_pkg_idle_wkups;
        public ulong ri_interrupt_wkups;
        public ulong ri_pageins;
        public ulong ri_wired_size;
        public ulong ri_resident_size;
        public ulong ri_phys_footprint;
        public ulong ri_phys_footprint_lifetime_max;
        public ulong ri_proc_start_abstime;
        public ulong ri_proc_exit_abstime;
    }
}

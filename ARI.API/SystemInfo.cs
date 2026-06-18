using ARI.Common;
using ARI.LLM;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ARI.API;

public record RamSegment(string Label, string ServerName, long Bytes);

/// <summary>Provides system RAM telemetry for the control panel.</summary>
public class SystemInfo
{
    private LLMModule? _llm => (LLMModule?)Modules.Llm;
    private readonly string _modelsPath;

    public SystemInfo(string modelsPath)
    {
        _modelsPath = modelsPath;
    }

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

        var serverEntries = new List<(string Name, long FileBytes, int ContextSize)>();

        if (_llm is not null)
        {
            foreach (Server server in _llm.Servers)
            {
                if (server.Status != ServerStatus.Online || server.Pid <= 0 || server.ActiveModel is null)
                    continue;

                string modelFile = Path.Combine(_modelsPath, server.ActiveModel.Name + ".gguf");
                long   fileBytes = File.Exists(modelFile) ? new FileInfo(modelFile).Length : 0;
                if (fileBytes <= 0) continue;

                serverEntries.Add((server.Name, fileBytes, server.ContextSize));
                accounted += fileBytes;
            }
        }

        long pythonBytes = 0;
        foreach (Process p in Process.GetProcessesByName("python").Concat(Process.GetProcessesByName("python3")))
            try { pythonBytes += PhysFootprint(p.Id); } catch { }
        accounted += pythonBytes;

        const long OsBaselineBytes = 2_684_354_560L;
        long kvPool   = Math.Max(0, totalSystem - accounted - OsBaselineBytes);
        long totalCtx = serverEntries.Sum(e => (long)e.ContextSize);

        var segments = new List<RamSegment>();
        foreach (var e in serverEntries)
        {
            segments.Add(new RamSegment(e.Name, e.Name, e.FileBytes));
            if (kvPool > 0 && totalCtx > 0)
            {
                long kvBytes = kvPool * e.ContextSize / totalCtx;
                if (kvBytes > 0) segments.Add(new RamSegment($"{e.Name} KV cache", e.Name, kvBytes));
            }
        }
        if (kvPool > 0 && totalCtx == 0)
            segments.Add(new RamSegment("KV cache", "System", kvPool));

        if (pythonBytes > 0) segments.Add(new RamSegment("F5-TTS", "TTS", pythonBytes));
        return segments;
    }

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

    private static long PhysFootprint(int pid)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try { return Process.GetProcessById(pid).WorkingSet64; }
            catch { return 0; }
        }

        try
        {
            var info   = new RUsageInfoV0();
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
        public ulong ri_user_time, ri_system_time, ri_pkg_idle_wkups, ri_interrupt_wkups;
        public ulong ri_pageins, ri_wired_size, ri_resident_size;
        public ulong ri_phys_footprint, ri_phys_footprint_lifetime_max;
        public ulong ri_proc_start_abstime, ri_proc_exit_abstime;
    }
}

using ARI.Common;
using ARI.LLM;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARI.API;

/// <param name="Kind">weights, kv, python or other.</param>
public record RamSegment(string Label, string ServerName, long Bytes, string Kind);

/// <summary>Provides system RAM telemetry for the control panel.</summary>
public class SystemInfo
{
    private LLMModule? _llm => (LLMModule?)Modules.Llm;
    private readonly string modelsPath;

    public SystemInfo(string modelsPath)
    {
        this.modelsPath = modelsPath;
    }

    /// <summary>Memory in use on the machine (on Linux in a container with a memory limit, the container's), in bytes.</summary>
    public long GetTotalRamBytes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && LinuxUsedBytes() is long linuxUsed) return linuxUsed;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && WindowsMemory() is { } win) return win.Total - win.Available;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Process.GetCurrentProcess().WorkingSet64;

        try
        {
            // Total physical unified memory (CPU + GPU share the same pool on Apple Silicon)
            ProcessStartInfo psiHw = new ProcessStartInfo("/bin/sh", "-c \"sysctl -n hw.memsize\"")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using Process hw = Process.Start(psiHw)!;
            string hwOut = hw.StandardOutput.ReadToEnd().Trim();
            hw.WaitForExit();
            if (!long.TryParse(hwOut, out long totalPhysical))
                throw new Exception("hw.memsize parse failed");

            // Free + speculative pages are the only truly unused unified memory
            ProcessStartInfo psiVm = new ProcessStartInfo("/bin/sh", "-c \"vm_stat\"")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using Process p = Process.Start(psiVm)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            long pageSize = 16384;
            long free = 0, speculative = 0;
            foreach (string line in output.Split('\n'))
            {
                if (line.StartsWith("Mach Virtual Memory Statistics") && line.Contains("page size of "))
                {
                    int s = line.IndexOf("page size of ") + 13;
                    int e = line.IndexOf(" bytes", s);
                    if (e > s && long.TryParse(line[s..e], out long ps)) pageSize = ps;
                }
                static long Pages(string l) { string v = l[(l.LastIndexOf(':') + 1)..].Trim().TrimEnd('.'); return long.TryParse(v, out long n) ? n : 0; }
                if (line.StartsWith("Pages free:"))        free        = Pages(line);
                if (line.StartsWith("Pages speculative:")) speculative = Pages(line);
            }
            return totalPhysical - (free + speculative) * pageSize;
        }
        catch { return Process.GetCurrentProcess().WorkingSet64; }
    }

    /// <summary>What the memory in use is made of, measured. A llama server's model (and vision) file is memory-mapped, so its weights are
    /// the files' size; its own footprint (phys_footprint, which leaves out mapped files) is the KV cache plus working buffers. Python services
    /// are each process's footprint, counting a process once even when its workers are also Python. Whatever is left of the total is
    /// "Other apps and file cache", so the segments always add up to <see cref="GetTotalRamBytes"/>.</summary>
    public List<RamSegment> GetRamBreakdown()
    {
        long totalSystem = GetTotalRamBytes();
        List<RamSegment> segments = new List<RamSegment>();

        if (_llm is not null)
        {
            foreach (Server server in _llm.Servers)
            {
                if (server.Status != ServerStatus.Online || server.Pid <= 0 || server.ActiveModel is null)
                    continue;

                long weights = FileBytes(server.ActiveModel.Path);
                if (server.VisionEnabled) weights += FileBytes(server.ActiveModel.MmprojPath);
                long working = PhysFootprint(server.Pid);

                if (weights > 0) segments.Add(new RamSegment(server.Name, server.Name, weights, "weights"));
                if (working > 0) segments.Add(new RamSegment(weights > 0 ? $"{server.Name} KV cache" : server.Name, server.Name, working, weights > 0 ? "kv" : "weights"));
            }
        }

        long pythonBytes = PythonServicesBytes();
        if (pythonBytes > 0) segments.Add(new RamSegment("Python services (voice, listener, images)", "Python", pythonBytes, "python"));

        long rest = totalSystem - segments.Sum(x => x.Bytes);
        if (rest > 0 && segments.Count > 0) segments.Add(new RamSegment("Other apps and file cache", "System", rest, "other"));
        return segments;
    }

    private long FileBytes(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return 0;
        try { string full = Path.Combine(modelsPath, relative); return File.Exists(full) ? new FileInfo(full).Length : 0; }
        catch { return 0; }
    }

    // Sum of the top-level Python processes. Forked workers (e.g. DataLoader) report the parent's Metal allocations again, so a Python process
    // whose parent is also Python is skipped.
    private static long PythonServicesBytes()
    {
        List<Process> pythons = Process.GetProcessesByName("python")
            .Concat(Process.GetProcessesByName("python3"))
            .Concat(Process.GetProcessesByName("Python")).ToList();
        if (pythons.Count == 0) return 0;
        HashSet<int> ids = pythons.Select(p => p.Id).ToHashSet();
        Dictionary<int, int> parents = ParentIds();
        long total = 0;
        foreach (Process p in pythons)
            try { if (!(parents.TryGetValue(p.Id, out int parent) && ids.Contains(parent))) total += PhysFootprint(p.Id); } catch { }
        return total;
    }

    private static Dictionary<int, int> ParentIds()
    {
        Dictionary<int, int> map = new Dictionary<int, int>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return map;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("/bin/ps", "-axo pid=,ppid=") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using Process ps = Process.Start(psi)!;
            string output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit();
            foreach (string line in output.Split('\n'))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out int pid) && int.TryParse(parts[1], out int ppid)) map[pid] = ppid;
            }
        }
        catch { }
        return map;
    }

    public double GetSwapMb()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try { return MemInfoSwapMb(File.ReadAllText("/proc/meminfo")); }
            catch { return 0; }
        }
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return 0;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("/bin/sh", "-c \"sysctl vm.swapusage\"")
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

    public record GpuInfo(string Name, long VramBytes);

    public List<GpuInfo> GetGpus()
    {
        List<GpuInfo> gpus = new List<GpuInfo>();
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Apple Silicon shares unified memory — report total physical RAM as "VRAM"
                ProcessStartInfo psi = new ProcessStartInfo("/bin/sh", "-c \"system_profiler SPDisplaysDataType -json\"")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using Process p = Process.Start(psi)!;
                string json = p.StandardOutput.ReadToEnd();
                p.WaitForExit();

                using JsonDocument doc = JsonDocument.Parse(json);
                // Fetch total physical RAM once — Apple Silicon shares it as unified GPU/CPU memory
                long hwMemsize = 0;
                ProcessStartInfo hwPsi = new ProcessStartInfo("/bin/sh", "-c \"sysctl -n hw.memsize\"")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using (Process hw = Process.Start(hwPsi)!)
                {
                    string hwOut = hw.StandardOutput.ReadToEnd().Trim();
                    hw.WaitForExit();
                    long.TryParse(hwOut, out hwMemsize);
                }

                foreach (JsonElement display in doc.RootElement.GetProperty("SPDisplaysDataType").EnumerateArray())
                {
                    string name = display.TryGetProperty("sppci_model", out JsonElement n) ? n.GetString() ?? "Unknown" : "Unknown";

                    long vram = 0;
                    bool builtin = display.TryGetProperty("sppci_bus", out JsonElement bus)
                                   && bus.GetString()?.Contains("builtin", StringComparison.OrdinalIgnoreCase) == true;

                    if (builtin)
                    {
                        // Apple Silicon — unified memory pool shared between CPU and GPU
                        vram = hwMemsize;
                    }
                    else if (display.TryGetProperty("sppci_vram", out JsonElement vramStr))
                    {
                        string vs = vramStr.GetString() ?? "";
                        Match match = Regex.Match(vs, @"(\d+)\s*(MB|GB)", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            long val = long.Parse(match.Groups[1].Value);
                            vram = match.Groups[2].Value.Equals("GB", StringComparison.OrdinalIgnoreCase) ? val * 1073741824 : val * 1048576;
                        }
                    }
                    gpus.Add(new GpuInfo(name, vram));
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Try nvidia-smi first for NVIDIA GPUs (accurate VRAM)
                try
                {
                    ProcessStartInfo nv = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits")
                        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                    using Process nvp = Process.Start(nv)!;
                    string nvOut = nvp.StandardOutput.ReadToEnd();
                    nvp.WaitForExit();
                    if (nvp.ExitCode == 0)
                    {
                        foreach (string line in nvOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            string[] parts = line.Split(',');
                            if (parts.Length >= 2)
                            {
                                string gpuName = parts[0].Trim();
                                long vramMb = long.TryParse(parts[1].Trim(), out long mb) ? mb : 0;
                                gpus.Add(new GpuInfo(gpuName, vramMb * 1048576));
                            }
                        }
                    }
                }
                catch { }

                // Fallback to WMI for non-NVIDIA or if nvidia-smi failed
                if (gpus.Count == 0)
                {
                    ProcessStartInfo wmi = new ProcessStartInfo("cmd.exe", "/c wmic path win32_VideoController get Name,AdapterRAM /format:csv")
                        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                    using Process wp = Process.Start(wmi)!;
                    string wmiOut = wp.StandardOutput.ReadToEnd();
                    wp.WaitForExit();
                    foreach (string line in wmiOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] cols = line.Split(',');
                        if (cols.Length >= 3 && cols[1].Trim() != "AdapterRAM")
                        {
                            long adapterRam = long.TryParse(cols[1].Trim(), out long ar) ? ar : 0;
                            string gpuName = cols[2].Trim();
                            if (!string.IsNullOrEmpty(gpuName))
                                gpus.Add(new GpuInfo(gpuName, adapterRam));
                        }
                    }
                }
            }
            else // Linux
            {
                try
                {
                    ProcessStartInfo nv = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits")
                        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                    using Process nvp = Process.Start(nv)!;
                    string nvOut = nvp.StandardOutput.ReadToEnd();
                    nvp.WaitForExit();
                    if (nvp.ExitCode == 0)
                    {
                        foreach (string line in nvOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            string[] parts = line.Split(',');
                            if (parts.Length >= 2)
                            {
                                string gpuName = parts[0].Trim();
                                long vramMb = long.TryParse(parts[1].Trim(), out long mb) ? mb : 0;
                                gpus.Add(new GpuInfo(gpuName, vramMb * 1048576));
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return gpus;
    }

    public long GetTotalPhysicalRamBytes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("/bin/sh", "-c \"sysctl -n hw.memsize\"")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using Process p = Process.Start(psi)!;
                string output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                if (long.TryParse(output, out long bytes)) return bytes;
            }
            catch { }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c wmic ComputerSystem get TotalPhysicalMemory /format:csv")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using Process p = Process.Start(psi)!;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                foreach (string line in output.Split('\n'))
                {
                    string[] cols = line.Split(',');
                    if (cols.Length >= 2 && long.TryParse(cols[^1].Trim(), out long bytes))
                        return bytes;
                }
            }
            catch { }
        }
        else // Linux: the machine's memory, or the container's limit when it has one
        {
            try
            {
                if (MemInfo(File.ReadAllText("/proc/meminfo")).TryGetValue("MemTotal", out long total))
                    return CgroupLimit(CgroupDir) is long limit && limit < total ? limit : total;
            }
            catch { }
        }
        return 0;
    }

    // ── Linux ─────────────────────────────────────────────────────────────────────────────────────────────────
    // /proc/meminfo describes the whole machine (inside Docker too). On a machine whose GPU shares system memory, such as a
    // DGX Spark, the models a server loads come out of this same pool, so "in use" includes them. A container started with a
    // memory limit is measured against that limit instead, from its cgroup.

    private const string CgroupDir = "/sys/fs/cgroup";

    private static long? LinuxUsedBytes()
    {
        try
        {
            if (CgroupLimit(CgroupDir) is not null && CgroupUsage(CgroupDir) is long inContainer) return inContainer;
            return MemInfoUsed(File.ReadAllText("/proc/meminfo"));
        }
        catch { return null; }
    }

    /// <summary>/proc/meminfo's fields in bytes (it gives kB).</summary>
    internal static Dictionary<string, long> MemInfo(string text)
    {
        Dictionary<string, long> fields = new(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string value = line[(colon + 1)..].Trim();
            if (value.EndsWith("kB", StringComparison.Ordinal)) value = value[..^2].Trim();
            if (long.TryParse(value, out long kb)) fields[line[..colon]] = kb * 1024;
        }
        return fields;
    }

    internal static long? MemInfoUsed(string text)
    {
        Dictionary<string, long> m = MemInfo(text);
        return m.TryGetValue("MemTotal", out long total) && m.TryGetValue("MemAvailable", out long available) ? total - available : null;
    }

    internal static double MemInfoSwapMb(string text)
    {
        Dictionary<string, long> m = MemInfo(text);
        return m.TryGetValue("SwapTotal", out long total) && m.TryGetValue("SwapFree", out long free) ? Math.Max(0, total - free) / 1024.0 / 1024.0 : 0;
    }

    /// <summary>The container's memory limit in bytes, or null when there is none ("max" in cgroup v2, a huge number in v1).</summary>
    internal static long? CgroupLimit(string dir)
    {
        string? v2 = ReadTrimmed(Path.Combine(dir, "memory.max"));
        if (v2 is not null) return long.TryParse(v2, out long limit) ? limit : null;
        string? v1 = ReadTrimmed(Path.Combine(dir, "memory", "memory.limit_in_bytes"));
        return long.TryParse(v1, out long v1Limit) && v1Limit < (1L << 60) ? v1Limit : null;
    }

    internal static long? CgroupUsage(string dir) =>
        long.TryParse(ReadTrimmed(Path.Combine(dir, "memory.current")) ?? ReadTrimmed(Path.Combine(dir, "memory", "memory.usage_in_bytes")), out long used) ? used : null;

    private static string? ReadTrimmed(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    // ── Windows ───────────────────────────────────────────────────────────────────────────────────────────────

    private static (long Total, long Available)? WindowsMemory()
    {
        MemoryStatusEx status = new() { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        try { return GlobalMemoryStatusEx(ref status) ? ((long)status.ullTotalPhys, (long)status.ullAvailPhys) : null; }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint  dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static long PhysFootprint(int pid)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try { return Process.GetProcessById(pid).WorkingSet64; }
            catch { return 0; }
        }

        try
        {
            RUsageInfoV0 info   = new RUsageInfoV0();
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

using ARI.Common;
using ARI.LLM;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARI.API;

public record RamSegment(string Label, string ServerName, long Bytes);

/// <summary>Provides system RAM telemetry for the control panel.</summary>
public class SystemInfo
{
    private LLMModule? _llm => (LLMModule?)Modules.Llm;
    private readonly string modelsPath;

    public SystemInfo(string modelsPath)
    {
        this.modelsPath = modelsPath;
    }

    public long GetTotalRamBytes()
    {
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

    public List<RamSegment> GetRamBreakdown()
    {
        long totalSystem = GetTotalRamBytes();
        long accounted   = 0;

        List<(string Name, long FileBytes, int ContextSize)> serverEntries = new List<(string Name, long FileBytes, int ContextSize)>();

        // PID-keyed: servers whose RAM we've already measured via PhysFootprint
        List<RamSegment> pidMeasuredSegments = new List<RamSegment>();

        if (_llm is not null)
        {
            foreach (Server server in _llm.Servers)
            {
                if (server.Status != ServerStatus.Online || server.Pid <= 0 || server.ActiveModel is null)
                    continue;

                string modelFile = Path.Combine(modelsPath, server.ActiveModel.Path);
                long   fileBytes = File.Exists(modelFile) ? new FileInfo(modelFile).Length : 0;

                if (fileBytes > 0)
                {
                    serverEntries.Add((server.Name, fileBytes, server.ContextSize));
                    accounted += fileBytes;
                }
                else
                {
                    // File path unknown — measure the process directly (model + KV combined)
                    long pidBytes = PhysFootprint(server.Pid);
                    if (pidBytes > 0)
                    {
                        pidMeasuredSegments.Add(new RamSegment(server.Name, server.Name, pidBytes));
                        accounted += pidBytes;
                    }
                }
            }
        }

        // Take the largest Python process only — DataLoader workers fork from the main process
        // and each independently reports the same Metal/MPS allocations in phys_footprint.
        long pythonBytes = 0;
        foreach (Process p in Process.GetProcessesByName("python")
            .Concat(Process.GetProcessesByName("python3"))
            .Concat(Process.GetProcessesByName("Python")))
            try { long fp = PhysFootprint(p.Id); if (fp > pythonBytes) pythonBytes = fp; } catch { }
        accounted += pythonBytes;

        const long OsBaselineBytes = 2_684_354_560L;
        long kvPool   = Math.Max(0, totalSystem - accounted - OsBaselineBytes);
        long totalCtx = serverEntries.Sum(e => (long)e.ContextSize);

        List<RamSegment> segments = new List<RamSegment>();

        // File-size tracked servers: split kvPool proportionally by context size
        foreach ((string Name, long FileBytes, int ContextSize) e in serverEntries)
        {
            segments.Add(new RamSegment(e.Name, e.Name, e.FileBytes));
            if (kvPool > 0 && totalCtx > 0)
            {
                long kvBytes = kvPool * e.ContextSize / totalCtx;
                if (kvBytes > 0) segments.Add(new RamSegment($"{e.Name} KV cache", e.Name, kvBytes));
            }
        }

        // PID-measured servers: process footprint = model weights; split remaining kvPool equally
        if (pidMeasuredSegments.Count > 0)
        {
            long kvPerPidServer = kvPool > 0 ? kvPool / pidMeasuredSegments.Count : 0;
            foreach (RamSegment seg in pidMeasuredSegments)
            {
                segments.Add(seg);
                if (kvPerPidServer > 0)
                    segments.Add(new RamSegment($"{seg.ServerName} KV cache", seg.ServerName, kvPerPidServer));
            }
        }
        else if (kvPool > 0 && serverEntries.Count == 0)
        {
            segments.Add(new RamSegment("KV cache", "System", kvPool));
        }

        if (pythonBytes > 0) segments.Add(new RamSegment("StyleTTS2", "StyleTTS2", pythonBytes));
        return segments;
    }

    public double GetSwapMb()
    {
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
        else // Linux
        {
            try
            {
                string meminfo = System.IO.File.ReadAllText("/proc/meminfo");
                foreach (string line in meminfo.Split('\n'))
                {
                    if (line.StartsWith("MemTotal:"))
                    {
                        string val = line["MemTotal:".Length..].Trim().Replace("kB", "").Trim();
                        if (long.TryParse(val, out long kb)) return kb * 1024;
                    }
                }
            }
            catch { }
        }
        return 0;
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

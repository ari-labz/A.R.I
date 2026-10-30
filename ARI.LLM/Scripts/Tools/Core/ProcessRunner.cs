using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ARI.Common;
using System.Text.Json;

namespace ARI.LLM;

/// <summary>Runs a process and collects what it prints, with a time limit, for the shell and code tools.</summary>
internal static class ProcessRunner
{
    private const int MaxChars = 40_000;

    internal sealed record Result(string Output, int ExitCode, bool TimedOut, bool Cancelled);

    internal static ProcessStartInfo ShellInfo(string command, string cwd)
    {
        ProcessStartInfo psi = OperatingSystem.IsWindows() ? new("cmd.exe") : new("/bin/bash");
        if (OperatingSystem.IsWindows()) { psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(command); }
        else { psi.ArgumentList.Add("-lc"); psi.ArgumentList.Add(command); }   // a login shell, so PATH matches the user's terminal
        return Prepare(psi, cwd);
    }

    internal static ProcessStartInfo Prepare(ProcessStartInfo psi, string cwd)
    {
        psi.WorkingDirectory       = cwd;
        psi.UseShellExecute        = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        psi.RedirectStandardInput  = true;   // closed straight away, so nothing waits for input that will never come
        psi.Environment["NO_COLOR"]  = "1";
        psi.Environment["TERM"]      = "dumb";
        psi.Environment["PAGER"]     = "cat";
        psi.Environment["GIT_PAGER"] = "cat";
        return psi;
    }

    internal static async Task<Result> Run(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
    {
        using Process p = Process.Start(psi)!;
        StringBuilder sb = new();
        object gate = new();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) sb.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (gate) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { p.StandardInput.Close(); } catch { }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        bool timedOut = false, cancelled = false;
        try { await p.WaitForExitAsync(cts.Token); p.WaitForExit(); }   // the second call lets the output events finish
        catch (OperationCanceledException)
        {
            cancelled = ct.IsCancellationRequested;
            timedOut  = !cancelled;
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        string output; lock (gate) output = sb.ToString();
        return new Result(Trim(output), p.HasExited ? p.ExitCode : -1, timedOut, cancelled);
    }

    /// <summary>Keeps the start and the end of long output, where the command line and the error usually are.</summary>
    internal static string Trim(string text)
    {
        text = text.TrimEnd();
        if (text.Length <= MaxChars) return text;
        return text[..12_000] + $"\n… [{text.Length - 38_000:N0} characters omitted] …\n" + text[^26_000..];
    }

    internal static string Format(Result r)
    {
        string body = r.Output.Length == 0 ? "(no output)" : r.Output;
        if (r.Cancelled) return body + "\n[stopped]";
        if (r.TimedOut)  return body + "\n[timed out and was stopped]";
        return body + $"\n[exit code {r.ExitCode}]";
    }
}

/// <summary>Whether a command needs the owner's approval before it runs. Plain read-only commands go straight
/// through; anything else asks in the app (the same Allow / Deny prompt git uses) unless "approve everything"
/// is switched on.</summary>
internal static class ShellGate
{
    private static string SettingsFile => Path.Combine(Paths.PersistentData, "shell_settings.json");

    private sealed record Settings(bool AutoApprove, List<string> Allow);

    private static Settings Read()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return new(false, new());
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
            bool auto = doc.RootElement.TryGetProperty("autoApprove", out JsonElement v) && v.ValueKind == JsonValueKind.True;
            List<string> allow = doc.RootElement.TryGetProperty("allow", out JsonElement a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(e => (e.GetString() ?? "").Trim()).Where(x => x.Length > 0).Distinct().ToList() : new();
            return new(auto, allow);
        }
        catch { return new(false, new()); }
    }

    internal static bool AutoApprove => Read().AutoApprove;
    internal static List<string> AllowList => Read().Allow;

    internal static void Save(bool autoApprove, IEnumerable<string> allow)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        List<string> clean = allow.Select(x => x.Trim()).Where(x => x.Length > 0 && x.Length <= 200).Distinct().Take(500).ToList();
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new { autoApprove, allow = clean }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static void SetAutoApprove(bool on) => Save(on, AllowList);

    /// <summary>True when every part of the command is either read-only or starts with something on the owner's allowed list ("git commit",
    /// "npm run build"). Command substitution and process substitution always ask, since they can hide another command.</summary>
    internal static bool IsAllowed(string command, List<string> allow)
    {
        if (command.Contains('`') || command.Contains("$(") || command.Contains("<(")) return false;
        foreach (string segment in System.Text.RegularExpressions.Regex.Split(command, @"\|\||&&|;|\||\n"))
        {
            string seg = segment.Trim();
            if (seg.Length == 0 || IsReadOnly(seg)) continue;
            if (!allow.Any(a => seg.Equals(a, StringComparison.Ordinal) || seg.StartsWith(a + " ", StringComparison.Ordinal))) return false;
        }
        return true;
    }

    private static readonly HashSet<string> ReadOnly = new(StringComparer.Ordinal)
    {
        "ls", "pwd", "echo", "date", "whoami", "uname", "which", "df", "du", "wc", "head", "tail", "sort", "uniq", "grep", "rg",
        "find", "tree", "file", "stat", "ps", "env", "printenv", "hostname", "uptime", "id", "cd", "true", "false", "basename", "dirname",
    };
    private static readonly HashSet<string> ReadOnlyGit = new(StringComparer.Ordinal) { "status", "log", "diff", "show", "branch", "rev-parse", "ls-files", "remote" };

    internal static bool IsReadOnly(string command)
    {
        if (command.Contains('>') || command.Contains('`') || command.Contains("$(") || command.Contains("<(") || command.Contains(" -delete") || command.Contains(" -exec")) return false;
        foreach (string segment in System.Text.RegularExpressions.Regex.Split(command, @"\|\||&&|;|\||\n"))
        {
            string[] words = segment.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;
            if (words[0] == "git") { if (words.Length < 2 || !ReadOnlyGit.Contains(words[1])) return false; }
            else if (!ReadOnly.Contains(words[0])) return false;
        }
        return true;
    }

    /// <summary>Null when the command may run; otherwise the message to give Ari.</summary>
    internal static async Task<string?> Approve(Thread? thread, string title, string command, bool allowListed = true)
    {
        if (thread is null || !thread.IsAdminChat) return "Only the owner, chatting in the app, can run commands.";
        Settings settings = Read();
        if (settings.AutoApprove || IsReadOnly(command) || (allowListed && IsAllowed(command, settings.Allow))) return null;
        return await ToolApprovals.RequestAsync(thread, title, command) ? null : "The owner didn't approve this, so it was not run. Ask them what they'd like instead.";
    }
}

/// <summary>The shell approval setting, for the control panel.</summary>
public static class ShellGateAccess
{
    public static bool AutoApprove => ShellGate.AutoApprove;
    public static List<string> AllowList => ShellGate.AllowList;
    public static void Save(bool autoApprove, IEnumerable<string> allow) => ShellGate.Save(autoApprove, allow);
}

/// <summary>Commands started in the background, so a server or a long job can run while Ari carries on.</summary>
internal static class ShellJobs
{
    internal sealed class Job
    {
        internal required int Id; internal required string Command; internal required DateTime Started;
        internal required Process Process; internal readonly StringBuilder Output = new();
        internal bool Running => !Process.HasExited;
    }

    private static readonly ConcurrentDictionary<int, Job> jobs = new();
    private static int next;

    static ShellJobs() => AppDomain.CurrentDomain.ProcessExit += (_, _) => { foreach (Job j in jobs.Values) try { if (j.Running) j.Process.Kill(entireProcessTree: true); } catch { } };

    internal static Job Start(ProcessStartInfo psi, string command)
    {
        Process p = Process.Start(psi)!;
        Job job = new() { Id = Interlocked.Increment(ref next), Command = command, Started = DateTime.Now, Process = p };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (job.Output) { job.Output.AppendLine(e.Data); if (job.Output.Length > 200_000) job.Output.Remove(0, 50_000); } };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (job.Output) { job.Output.AppendLine(e.Data); if (job.Output.Length > 200_000) job.Output.Remove(0, 50_000); } };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        try { p.StandardInput.Close(); } catch { }
        jobs[job.Id] = job;
        return job;
    }

    internal static Job? Get(int id) => jobs.TryGetValue(id, out Job? j) ? j : null;
    internal static IEnumerable<Job> All => jobs.Values.OrderBy(j => j.Id);

    internal static string Describe(Job j) =>
        $"job {j.Id}: {(j.Running ? "running" : $"finished (exit {j.Process.ExitCode})")} · started {j.Started:HH:mm:ss} · {(j.Command.Length > 80 ? j.Command[..80] + "…" : j.Command)}";

    internal static string Tail(Job j, int chars = 8000)
    {
        string text; lock (j.Output) text = j.Output.ToString().TrimEnd();
        return text.Length <= chars ? (text.Length == 0 ? "(no output yet)" : text) : "…" + text[^chars..];
    }
}

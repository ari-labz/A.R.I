using System.Collections.Concurrent;
using System.Text;
namespace ARI.LLM;

[Tool("shell", "Run a command on the machine ARI runs on, in this conversation's folder: programs, packages, dev servers, git. " +
               "(For the user's own computer use run_command, which needs the desktop app.) Anything that isn't read-only asks the owner first. " +
               "Commit, push, merge or tag only when asked. A refused command means stop and ask; never try it another way.", TimeoutSeconds = 700)]
public class Shell : Tool
{
    [Param("The command line, exactly as you'd type it in a terminal.")]
    public string Command;

    [Param("Folder to run it in. Defaults to this conversation's working folder.", Optional = true)]
    public string Cwd;

    [Param("Give up after this many seconds. Default 60, at most 600.", Optional = true)]
    public int TimeoutSeconds = 60;

    [Param("true to start it in the background and return at once. Use it for servers and long jobs, then check on it with the jobs tool. You are woken when it finishes, so you don't need to poll.", Optional = true)]
    public bool Background;

    [Param("Background only: text to watch for in its output, such as \"Listening on\" or \"error\". You are woken the first time a line contains it, even though the job is still running.", Optional = true)]
    public string NotifyWhen;

    public override async Task<string> Run()
    {
        string cwd = string.IsNullOrWhiteSpace(Cwd) ? WorkingFolder : Path.IsPathRooted(Cwd) ? Cwd : Path.Combine(WorkingFolder, Cwd);
        if (!Directory.Exists(cwd)) return $"[Error: the folder {cwd} doesn't exist.]";
        if (await ShellGate.Approve(CallThread, "Run a command", Command) is { } refusal) return refusal;

        System.Diagnostics.ProcessStartInfo psi = ProcessRunner.ShellInfo(Command, cwd);
        if (Background)
        {
            ShellJobs.Job job = ShellJobs.Start(psi, Command, CallThread?.Key, NotifyWhen);
            return $"Started job {job.Id} in the background. Check on it or stop it with the jobs tool (id \"shell #{job.Id}\").";
        }
        ProcessRunner.Result r = await ProcessRunner.Run(psi, TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 600)), Cancelled);
        return ProcessRunner.Format(r);
    }
}

[Tool("shell_job", "Check on, read the output of, or stop a command you started in the background with shell.")]
public class ShellJob : Tool
{
    [Param("list, read or kill", Options = "list,read,kill")]
    public string Action;

    [Param("The job number from shell. Not needed for list.", Optional = true)]
    public int? Id;

    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can manage commands.";

    public override Task<string> Run()
    {
        if (Action == "list")
            return Task.FromResult(ShellJobs.All.Any() ? string.Join("\n", ShellJobs.All.Select(ShellJobs.Describe)) : "No background jobs.");
        if (Id is null) return Task.FromResult("[Error: give the job's id.]");
        if (ShellJobs.Get(Id.Value) is not { } job) return Task.FromResult($"[Error: there is no job {Id}.]");
        if (Action == "kill")
        {
            job.Stopped = true;   // stopped on purpose, so it doesn't wake her
            if (job.Running) try { job.Process.Kill(entireProcessTree: true); } catch { }
            return Task.FromResult($"Stopped job {job.Id}.");
        }
        return Task.FromResult($"{ShellJobs.Describe(job)}\n\n{ShellJobs.Tail(job)}");
    }
}

[Tool("run_code", "Run a short program and get back what it prints. Python, JavaScript (Node) or bash. " +
                  "Use it to calculate, process data, make a chart or a file, test an idea, or check something rather than guessing. " +
                  "It runs on the server in this conversation's working folder, so files it writes there can be read, edited and delivered. " +
                  "It asks the owner to approve each run.", TimeoutSeconds = 700)]
public class RunCode : Tool
{
    [Param("python, javascript or bash", Options = "python,javascript,bash")]
    public string Language;

    [Param("The program. Print what you want to see.")]
    public string Code;

    [Param("Give up after this many seconds. Default 60, at most 600.", Optional = true)]
    public int TimeoutSeconds = 60;

    public override async Task<string> Run()
    {
        if (await ShellGate.Approve(CallThread, $"Run {Language} code", Code, allowListed: false, codeKey: Language) is { } refusal) return refusal;

        (string exe, string ext) = Language switch
        {
            "python"     => (await ARI.Common.PythonRuntime.Default(Cancelled), ".py"),
            "javascript" => ("node", ".js"),
            _            => ("/bin/bash", ".sh"),
        };
        string dir = Path.Combine(Path.GetTempPath(), "ari-run");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"run-{Guid.NewGuid():N}{ext}");
        await File.WriteAllTextAsync(file, Code);
        try
        {
            System.Diagnostics.ProcessStartInfo psi = ProcessRunner.Prepare(new System.Diagnostics.ProcessStartInfo(exe), WorkingFolder);
            psi.ArgumentList.Add(file);
            ProcessRunner.Result r = await ProcessRunner.Run(psi, TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 600)), Cancelled);
            return ProcessRunner.Format(r);
        }
        catch (System.ComponentModel.Win32Exception) { return $"[Error: {exe} isn't installed on this machine, so {Language} can't run.]"; }
        finally { try { File.Delete(file); } catch { } }
    }
}

[Tool("jobs", "Everything running or finished in the background in one place: tools you started that came back as \"started in the background\", and commands started with shell in the background. " +
              "list shows them all, status shows one's state and its result or latest output, cancel stops one you no longer want.", Group = "task_tools")]
public class Jobs : Tool
{
    [Param("list, status or cancel", Options = "list,status,cancel")]
    public string Action;

    [Param("Which job, as the list names it: e.g. \"write_pdf #2\" or \"shell #1\". A bare number works when it is unambiguous. Not needed for list.", Optional = true)]
    public string Id;

    private static string ShellLine(ShellJobs.Job j) => $"shell #{j.Id}: {(j.Running ? "running" : $"finished (exit {j.Process.ExitCode})")} since {j.Started:HH:mm:ss} · {(j.Command.Length > 70 ? j.Command[..70] + "…" : j.Command)}";

    public override Task<string> Run()
    {
        string key = CallThread?.Key ?? "";
        List<AsyncToolJobs.Job> tools = AsyncToolJobs.For(key);
        List<ShellJobs.Job> shells = IsAdmin ? ShellJobs.All.ToList() : new();
        if (Action == "list")
        {
            List<string> lines = tools.Select(j => j.Summary).Concat(shells.Select(ShellLine)).ToList();
            return Task.FromResult(lines.Count == 0 ? "Nothing has been started in the background in this conversation." : string.Join("\n", lines));
        }
        if (string.IsNullOrWhiteSpace(Id)) return Task.FromResult("[Error: say which job. The list shows them.]");
        string id = Id.Trim();

        bool wantsShell = id.StartsWith("shell", StringComparison.OrdinalIgnoreCase);
        AsyncToolJobs.Job? tool = wantsShell ? null : AsyncToolJobs.Find(key, id);
        if (tool is not null)
        {
            if (Action == "cancel") return Task.FromResult(AsyncToolJobs.Cancel(tool));
            return Task.FromResult($"{tool.Summary}\n" + (tool.State == "running" ? "Still running; its result will reach you when it finishes." : $"Result:\n{tool.Result}"));
        }
        string digits = new(id.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out int n) && ShellJobs.Get(n) is { } shell)
        {
            if (!IsAdmin) return Task.FromResult("[Error: only the owner, chatting in the app, can manage commands.]");
            if (Action == "cancel")
            {
                if (shell.Running) try { shell.Process.Kill(entireProcessTree: true); } catch { }
                return Task.FromResult($"shell #{shell.Id} stopped.");
            }
            return Task.FromResult($"{ShellLine(shell)}\n\n{ShellJobs.Tail(shell)}");
        }
        return Task.FromResult($"[Error: there is no job \"{Id}\". Use list to see them.]");
    }
}

/// <summary>One long-lived shell per conversation, so the folder it is in, the variables it has set and the environment it activated survive from one command to the next.</summary>
internal static class ShellSessions
{
    private sealed class Session
    {
        internal required System.Diagnostics.Process Process;
        internal readonly StringBuilder Buffer = new();
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal DateTime LastUsed = DateTime.UtcNow;
        internal string Cwd = "";
    }

    private static readonly ConcurrentDictionary<string, Session> sessions = new();
    private const int MaxChars = 30_000;
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(30);

    static ShellSessions() => AppDomain.CurrentDomain.ProcessExit += (_, _) => { foreach (Session s in sessions.Values) try { s.Process.Kill(entireProcessTree: true); } catch { } };

    private static Session Get(string key, string cwd)
    {
        foreach ((string k, Session old) in sessions.Where(kv => DateTime.UtcNow - kv.Value.LastUsed > IdleLimit || kv.Value.Process.HasExited).ToList())
        { try { old.Process.Kill(entireProcessTree: true); } catch { } sessions.TryRemove(k, out _); }
        return sessions.GetOrAdd(key, _ =>
        {
            System.Diagnostics.ProcessStartInfo psi = OperatingSystem.IsWindows() ? new("cmd.exe") : new("/bin/bash");
            if (!OperatingSystem.IsWindows()) { psi.ArgumentList.Add("-l"); }
            System.Diagnostics.Process p = System.Diagnostics.Process.Start(ProcessRunner.Prepare(psi, cwd))!;
            Session s = new() { Process = p, Cwd = cwd };
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (s.Buffer) s.Buffer.AppendLine(e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (s.Buffer) s.Buffer.AppendLine(e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            return s;
        });
    }

    internal static bool Exists(string key) => sessions.TryGetValue(key, out Session? s) && !s.Process.HasExited;

    internal static string Close(string key)
    {
        if (!sessions.TryRemove(key, out Session? s)) return "There was no shell session open.";
        try { s.Process.Kill(entireProcessTree: true); } catch { }
        return "Shell session closed.";
    }

    /// <summary>Runs one command in the session and returns what it printed, its exit code and the folder the shell is now in.</summary>
    internal static async Task<string> Send(string key, string cwd, string command, TimeSpan timeout, CancellationToken ct)
    {
        Session s = Get(key, cwd);
        await s.Gate.WaitAsync(ct);
        try
        {
            if (OperatingSystem.IsWindows()) return "[Error: persistent shell sessions need a bash shell; they aren't available on Windows yet.]";
            s.LastUsed = DateTime.UtcNow;
            string id = Guid.NewGuid().ToString("N")[..10];
            lock (s.Buffer) s.Buffer.Clear();
            // The command, then a marker line carrying its exit code and the folder, so the end of its output is known exactly.
            await s.Process.StandardInput.WriteLineAsync($"{command}\nprintf '\\n__ARI_END_{id}_%s_%s\\n' \"$?\" \"$PWD\"");
            await s.Process.StandardInput.FlushAsync();

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            string marker = $"__ARI_END_{id}_";
            try
            {
                while (true)
                {
                    string text; lock (s.Buffer) text = s.Buffer.ToString();
                    int at = text.IndexOf(marker, StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        int eol = text.IndexOf('\n', at);
                        string[] meta = text[(at + marker.Length)..(eol < 0 ? text.Length : eol)].Split('_', 2);
                        if (eol >= 0 || meta.Length == 2)
                        {
                            s.Cwd = meta.Length > 1 ? meta[1] : s.Cwd;
                            return Format(text[..at].TrimEnd(), meta[0], s.Cwd, timedOut: false);
                        }
                    }
                    if (s.Process.HasExited) { sessions.TryRemove(key, out _); return Format(text.TrimEnd(), "?", s.Cwd, false) + "\n[The shell ended. The next command starts a fresh one.]"; }
                    await Task.Delay(40, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                string partial; lock (s.Buffer) partial = s.Buffer.ToString();
                return Format(partial.TrimEnd(), "?", s.Cwd, timedOut: true);
            }
        }
        finally { s.Gate.Release(); }
    }

    private static string Format(string output, string exit, string cwd, bool timedOut)
    {
        if (output.Length > MaxChars) output = "…(earlier output cut)…\n" + output[^MaxChars..];
        string head = timedOut ? "still running after the time limit; it keeps going in the session. Use read for more output, or close to stop it" : $"exit {exit}";
        return $"{(output.Length == 0 ? "(no output)" : output)}\n\n[{head} · now in {cwd}]";
    }

    /// <summary>What the session has printed since the last send, without sending anything (for a command that was still running).</summary>
    internal static string Read(string key)
    {
        if (!sessions.TryGetValue(key, out Session? s)) return "There is no shell session open.";
        string text; lock (s.Buffer) text = s.Buffer.ToString();
        text = string.Join('\n', text.Split('\n').Where(l => !l.StartsWith("__ARI_END_", StringComparison.Ordinal))).Trim();   // the end markers are ours
        if (text.Length > MaxChars) text = "…(earlier output cut)…\n" + text[^MaxChars..];
        return text.Length == 0 ? "(nothing new)" : text;
    }
}

[Tool("shell_session", "A shell that stays open between commands in this conversation, like a terminal you keep: the folder you cd into, variables you export and environments you activate are still there next time. " +
                       "send runs a command in it and returns its output, exit code and the folder it is now in. read shows output that arrived after a send timed out. close ends the session. " +
                       "Use shell for a one-off command; use this when steps depend on each other. It asks the owner to approve anything that isn't read-only, same as shell.", TimeoutSeconds = 700)]
public class ShellSession : Tool
{
    [Param("send, read or close", Options = "send,read,close")]
    public string Action;

    [Param("The command line to run. Needed for send.", Optional = true)]
    public string Command;

    [Param("Give up waiting after this many seconds (the command keeps running). Default 60, at most 600.", Optional = true)]
    public int TimeoutSeconds = 60;

    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can run commands.";

    public override async Task<string> Run()
    {
        string key = CallThread?.Key ?? "";
        if (Action == "close") return ShellSessions.Close(key);
        if (Action == "read") return ShellSessions.Read(key);
        if (string.IsNullOrWhiteSpace(Command)) return "[Error: give the command to send.]";
        if (await ShellGate.Approve(CallThread, "Run a command in the shell session", Command) is { } refusal) return refusal;
        return await ShellSessions.Send(key, WorkingFolder, Command, TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 600)), Cancelled);
    }
}

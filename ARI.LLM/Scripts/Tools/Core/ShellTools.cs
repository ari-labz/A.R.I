namespace ARI.LLM;

[Tool("shell", "Run a command in a shell on the machine ARI itself runs on (the server), in this conversation's working folder. " +
               "Use it to run programs, install packages, start a dev server, check what's installed, or work with git and the system. " +
               "To run something on the user's own computer instead, use run_command, which needs the desktop app. " +
               "It asks the owner to approve anything that isn't read-only.", TimeoutSeconds = 700)]
public class Shell : Tool
{
    [Param("The command line, exactly as you'd type it in a terminal.")]
    public string Command;

    [Param("Folder to run it in. Defaults to this conversation's working folder.", Optional = true)]
    public string Cwd;

    [Param("Give up after this many seconds. Default 60, at most 600.", Optional = true)]
    public int TimeoutSeconds = 60;

    [Param("true to start it in the background and return at once. Use it for servers and long jobs, then check on it with the jobs tool.", Optional = true)]
    public bool Background;

    public override async Task<string> Run()
    {
        string cwd = string.IsNullOrWhiteSpace(Cwd) ? WorkingFolder : Path.IsPathRooted(Cwd) ? Cwd : Path.Combine(WorkingFolder, Cwd);
        if (!Directory.Exists(cwd)) return $"[Error: the folder {cwd} doesn't exist.]";
        if (await ShellGate.Approve(CallThread, "Run a command", Command) is { } refusal) return refusal;

        System.Diagnostics.ProcessStartInfo psi = ProcessRunner.ShellInfo(Command, cwd);
        if (Background)
        {
            ShellJobs.Job job = ShellJobs.Start(psi, Command);
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
        if (await ShellGate.Approve(CallThread, $"Run {Language} code", Code, allowListed: false) is { } refusal) return refusal;

        (string exe, string ext) = Language switch
        {
            "python"     => (ARI.Common.PythonLocator.Find(), ".py"),
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

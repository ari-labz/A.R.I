using System.Diagnostics;

namespace ARI.LLM;

/// <summary>
/// Shared base for git tools that operate on a repository root. Generic — any agent can register these
/// against any git working tree (the memory agents use them over the vault). The tool set IS the git
/// interface; there is no domain-specific git wrapper behind it.
/// </summary>
internal abstract class GitTool : Tool
{
    protected readonly string root;

    protected GitTool(string root) => this.root = root;

    /// <summary>Run a git subcommand in the repo root. Optional stdin is written then closed (used for
    /// commit messages via `-F -`). Returns the exit code plus captured stdout/stderr.
    /// NOTE: distinct name from <see cref="Run(string[])"/> on purpose — a single overloaded `Run` makes
    /// `Run("status","--short")` bind "status" to the stdin parameter, silently breaking every git call.</summary>
    protected (int Code, string Out, string Err) RunInput(string? stdin, params string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName               = "git",
            WorkingDirectory       = root,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = stdin is not null,
            UseShellExecute        = false,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using Process process = Process.Start(psi)!;
        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }
        string outp = process.StandardOutput.ReadToEnd();
        string err  = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, outp, err);
    }

    protected (int Code, string Out, string Err) Run(params string[] args) => RunInput(null, args);
}

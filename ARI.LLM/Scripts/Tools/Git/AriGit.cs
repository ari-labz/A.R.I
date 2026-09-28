using System.Diagnostics;
using ARI.Common;

namespace ARI.LLM;

/// <summary>
/// The one place ARI's git commits are made — the git tool, the memory tools and Engram all commit through
/// <see cref="Commit"/>, so every commit she makes is signed the same way, per the user's control-panel
/// setting (<see cref="GitHubSettings.CommitAs"/>).
/// </summary>
internal static class AriGit
{
    internal const string Name  = "A.R.I";
    internal const string Email = "ari@xywren.net";
    internal static string Identity => $"{Name} <{Email}>";

    /// <summary>Commits whatever is staged in <paramref name="workDir"/>. Author mode makes ARI the commit's
    /// author (the machine's git user stays committer); co-author mode appends a Co-Authored-By trailer.</summary>
    internal static (int Code, string Out, string Err) Commit(string workDir, string message, IEnumerable<string>? extraArgs = null)
    {
        List<string> args = new();
        // A machine with no git identity (a fresh install) would reject the commit outright — let ARI stand in
        // as committer there rather than fail.
        (int _, string configuredEmail, string _) = Run(workDir, ["config", "user.email"]);
        if (string.IsNullOrWhiteSpace(configuredEmail))
            args.AddRange(["-c", $"user.name={Name}", "-c", $"user.email={Email}"]);

        args.AddRange(["commit", "-F", "-"]);
        if (GitHubStore.Get().CommitAs == CommitIdentity.Author)
            args.Add($"--author={Identity}");
        else if (!message.Contains($"Co-Authored-By: {Identity}", StringComparison.OrdinalIgnoreCase))
            message = $"{message.TrimEnd()}\n\nCo-Authored-By: {Identity}";
        if (extraArgs is not null) args.AddRange(extraArgs);

        return Run(workDir, args.ToArray(), message);
    }

    /// <summary>Runs git in <paramref name="workDir"/>. Optional stdin is written then closed (commit messages go
    /// through `-F -` so quotes and newlines survive intact).</summary>
    internal static (int Code, string Out, string Err) Run(string workDir, string[] args, string? stdin = null)
    {
        ProcessStartInfo psi = new()
        {
            FileName               = "git",
            WorkingDirectory       = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = stdin is not null,
            UseShellExecute        = false,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";   // never hang waiting for a password nobody will type
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using Process proc = Process.Start(psi)!;
        if (stdin is not null)
        {
            proc.StandardInput.Write(stdin);
            proc.StandardInput.Close();
        }
        Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.Result.Trim(), errTask.Result.Trim());
    }
}

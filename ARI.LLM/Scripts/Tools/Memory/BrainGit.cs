using System.Diagnostics;

namespace ARI.LLM;

// Shared git plumbing for the brain-note tools. Every note write is its own commit — one commit per
// note, message required from the caller, so the brain's history reads as "what changed and why" one
// entry at a time instead of one blob per Engram sweep.
internal static class BrainGit
{
    internal static string Commit(string vaultRoot, string message)
    {
        (int _, string status, string _) = Run(vaultRoot, "status", "--porcelain");
        if (string.IsNullOrWhiteSpace(status)) return "(No git changes detected.)";

        Run(vaultRoot, "add", "-A");
        (int code, string _, string err) = RunInput(vaultRoot, message, "commit", "-F", "-");
        if (code != 0) return $"Commit failed: {err.Trim()}";

        (int _, string head, string _) = Run(vaultRoot, "log", "-1", "--format=%h %s");
        return $"Committed to brain: {head.Trim()}";
    }

    private static (int Code, string Out, string Err) RunInput(string workDir, string? stdin, params string[] args)
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
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using Process process = Process.Start(psi)!;
        if (stdin is not null) { process.StandardInput.Write(stdin); process.StandardInput.Close(); }
        string outp = process.StandardOutput.ReadToEnd();
        string err  = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, outp, err);
    }

    private static (int Code, string Out, string Err) Run(string workDir, params string[] args)
        => RunInput(workDir, null, args);
}

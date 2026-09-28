namespace ARI.LLM;

// Shared git plumbing for the brain-note tools. Every note write is its own commit — one commit per
// note, message required from the caller, so the brain's history reads as "what changed and why" one
// entry at a time instead of one blob per Engram sweep.
internal static class BrainGit
{
    // relativePaths scopes the commit to exactly the file(s) this call touched (2 for a rename: the old
    // path being deleted and the new one being written) — `git add -A` would sweep in whatever else
    // happens to be sitting dirty in the vault (a manual Obsidian edit, a stray deletion from outside
    // any tool call) and silently misattribute it to this note's commit.
    internal static string Commit(string vaultRoot, string message, params string[] relativePaths)
    {
        (int _, string status, string _) = AriGit.Run(vaultRoot, ["status", "--porcelain"]);
        if (string.IsNullOrWhiteSpace(status)) return "(No git changes detected.)";

        foreach (string path in relativePaths) AriGit.Run(vaultRoot, ["add", "--", path]);
        (int code, string _, string err) = AriGit.Commit(vaultRoot, message);
        if (code != 0) return $"Commit failed: {err}";

        (int _, string head, string _) = AriGit.Run(vaultRoot, ["log", "-1", "--format=%h %s"]);
        return $"Committed to brain: {head}";
    }
}

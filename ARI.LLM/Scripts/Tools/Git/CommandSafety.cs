using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>
/// Shared argument handling and safety rules for the git and github tools: splitting a command string into
/// arguments, and recognising commands that destroy work or change things that are hard to undo. Those
/// need the user's approval before they run; a few gh commands that manage gh itself are never run.
/// </summary>
internal static class CommandSafety
{
    /// <summary>Space-separated, but a "double-quoted" or 'single-quoted' run stays one argument (paths with spaces).</summary>
    internal static string[] Split(string command)
        => Regex.Matches(command, "\"([^\"]*)\"|'([^']*)'|(\\S+)")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value)
            .ToArray();

    /// <summary>Why this git command needs approval, or null if it's safe to run.</summary>
    internal static string? GitApprovalReason(string command, IReadOnlyList<string> args)
    {
        bool Has(params string[] flags) => args.Any(a => flags.Contains(a));
        return command switch
        {
            "reset"   when Has("--hard", "--merge", "--keep")                            => "discards uncommitted changes",
            "push"    when Has("--force", "-f", "--force-with-lease", "--mirror", "--delete", "-d")
                          || args.Any(a => a.StartsWith(':') || a.StartsWith('+'))        => "rewrites or deletes history on the remote",
            "clean"                                                                      => "permanently deletes untracked files",
            "branch"  when Has("-D", "--delete", "-d", "--force", "-f")                  => "deletes a branch",
            "checkout" when Has("--force", "-f", ".", "--")                              => "discards uncommitted changes",
            "restore" when !Has("--staged") || Has("--worktree", "-W")                   => "discards uncommitted changes",
            "stash"   when args.FirstOrDefault() is "drop" or "clear"                     => "deletes stashed work",
            _ => null,
        };
    }

    /// <summary>gh commands ARI never runs: they manage gh's own login, config or plugins (and `auth token`
    /// would print the account's token into the conversation).</summary>
    internal static bool GhIsBlocked(IReadOnlyList<string> args)
        => args.FirstOrDefault() is "auth" or "config" or "extension" or "extensions" or "ext" or "alias";

    /// <summary>Why this gh command needs approval, or null if it's safe to run.</summary>
    internal static string? GhApprovalReason(IReadOnlyList<string> args)
    {
        string group = args.ElementAtOrDefault(0) ?? "";
        string verb  = args.ElementAtOrDefault(1) ?? "";
        if (group is "secret" or "variable" or "ssh-key" or "gpg-key")
            return "changes account or repository secrets/keys";
        if (verb is "delete" or "archive" or "unarchive")
            return $"{verb}s a {group}";
        if (group == "repo" && verb is "rename" || group == "repo" && verb == "edit" && args.Any(a => a.StartsWith("--visibility")))
            return "renames a repo or changes who can see it";
        if (group == "pr" && verb == "merge")
            return "merges a pull request";
        if (group == "api" && args.Select((a, i) => (a, i)).Any(p =>
                (p.a is "-X" or "--method" && args.ElementAtOrDefault(p.i + 1)?.ToUpperInvariant() == "DELETE")
                || p.a.Equals("--method=DELETE", StringComparison.OrdinalIgnoreCase) || p.a.Equals("-XDELETE", StringComparison.OrdinalIgnoreCase)))
            return "deletes something through the GitHub API";
        return null;
    }
}

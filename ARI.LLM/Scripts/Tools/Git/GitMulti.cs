using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// Multi-repo git tool. Discovers the project's repositories at construction time — the project folder
/// itself, plus any repos (including submodules) up to MAX_SCAN_DEPTH levels below it — and exposes them
/// as a named enum. ARI never constructs paths — she just picks a repo name and a command; the tool
/// resolves the path itself.
/// </summary>
internal sealed class GitMulti : Tool
{
    private const int MAX_SCAN_DEPTH = 2;
    private const int MAX_LABEL_CHARS = 60;
    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase) { "node_modules", "bin", "obj" };

    private readonly Dictionary<string, string> repos;  // display name → absolute path
    private readonly string? rootRepo;                  // the project folder's own repo, when it is one
    private readonly Thread  thread;                    // for approval prompts on destructive commands

    internal override string Name => "git";

    private GitMulti(Dictionary<string, string> repos, string? rootRepo, Thread thread)
    {
        this.repos    = repos;
        this.rootRepo = rootRepo;
        this.thread   = thread;
    }

    /// <summary>Finds every repo in the project: the root folder when it is one (named after the folder),
    /// and nested repos keyed by their path relative to the root. Returns null if none are found so
    /// ToolFactories can skip registration cleanly.</summary>
    internal static GitMulti? Discover(string projectRoot, Thread thread)
    {
        if (!Directory.Exists(projectRoot)) return null;

        Dictionary<string, string> repos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? rootRepo = IsRepo(projectRoot) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(projectRoot)) : null;
        if (rootRepo is not null) repos[rootRepo] = projectRoot;
        ScanForRepos(projectRoot, projectRoot, 1, repos);

        return repos.Count == 0 ? null : new GitMulti(repos, rootRepo, thread);
    }

    private static void ScanForRepos(string dir, string projectRoot, int depth, Dictionary<string, string> repos)
    {
        IEnumerable<string> subdirs;
        try { subdirs = Directory.EnumerateDirectories(dir).ToList(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }

        foreach (string subdir in subdirs)
        {
            string name = Path.GetFileName(subdir);
            if (name.StartsWith('.') || SkippedDirs.Contains(name)) continue;
            if (IsRepo(subdir))
                repos[Path.GetRelativePath(projectRoot, subdir).Replace('\\', '/')] = subdir;
            else if (depth < MAX_SCAN_DEPTH)
                ScanForRepos(subdir, projectRoot, depth + 1, repos);
        }
    }

    // .git is a folder in a normal clone but a file in a submodule or worktree — both are repos.
    private static bool IsRepo(string dir) => Path.Exists(Path.Combine(dir, ".git"));

    // The repo a call targets when it doesn't name one: the project folder itself if it's a repo, else the
    // only repo there is. Lets single-repo callers (the memory agents over the Brain) skip the parameter.
    private string? DefaultRepo => rootRepo ?? (repos.Count == 1 ? repos.Keys.First() : null);

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "git",
            description = $"Run git against one of the project's repositories. Repos: {string.Join(", ", repos.Keys)}"
                        + (DefaultRepo is { } d ? $" (default: {d})" : "") + ". "
                        + "Works with any host (GitHub, GitLab, a company server). "
                        + "fetch before pull to preview incoming changes; diff to review before committing. "
                        + "commit stages everything first if nothing is staged yet. Commit message: first line "
                        + "= what changed, then a blank line, then why. One commit per logical change.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    repo = new
                    {
                        type        = "string",
                        @enum       = repos.Keys.Order().ToArray(),
                        description = "Which repository to target. Optional when there's a default."
                    },
                    command = new
                    {
                        type        = "string",
                        @enum       = new[] { "status", "fetch", "pull", "log", "diff", "add", "commit", "push", "branch", "checkout", "stash", "restore" },
                        description = "git subcommand to run."
                    },
                    message = new
                    {
                        type        = "string",
                        description = "Commit message — required for commit. First line = what changed; blank line; then why."
                    },
                    args = new
                    {
                        type        = "string",
                        description = "Optional extra arguments (e.g. a file path for add/diff/log, a branch name for checkout, 'origin main' for push). Quote paths with spaces. add with no args stages everything; log with no args shows the last 15 commits in one line each."
                    }
                },
                required = new[] { "command" }
            }
        }
    };

    internal override Func<string, string>? Display => argsJson =>
    {
        JsonElement a = Parse(argsJson);
        string label = $"{Str(a, "command")} {Str(a, "args")}".Replace("\r", " ").Replace("\n", " ").Trim();
        if (label.Length > MAX_LABEL_CHARS) label = $"{label[..MAX_LABEL_CHARS]}…";
        // The marker grammar uses ':' '>' '|' and '--' as delimiters, so neutralise them in the label.
        return $"<!--ari-tool-start:git:{label.Replace("--", "&#45;&#45;").Replace(":", "∶").Replace(">", "&gt;").Replace("|", "¦")}-->";
    };

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        JsonElement a = Parse(argsJson);
        string repo    = Str(a, "repo");
        string command = Str(a, "command");
        string extra   = Str(a, "args");

        if (repo.Length == 0) repo = DefaultRepo ?? "";
        if (!repos.TryGetValue(repo, out string? repoPath))
            return $"Unknown repo '{repo}'. Available: {string.Join(", ", repos.Keys)}";

        if (command == "commit")
            return Commit(repoPath, Str(a, "message"), extra);

        string[] extraArgs = CommandSafety.Split(extra);
        if (CommandSafety.GitApprovalReason(command, extraArgs) is { } reason)
        {
            string shown = $"git {command} {extra}".Trim();
            if (!await ToolApprovals.RequestAsync(thread, $"ARI wants to run a git command in {repo} that {reason}.", shown))
                return $"Not run: this {reason}, and the user didn't approve it. If it's still needed, tell them the exact command so they can run it: {shown}";
        }

        List<string> args = new List<string> { command };
        if (command == "log" && string.IsNullOrWhiteSpace(extra))
            args.AddRange(["-n15", "--oneline"]);
        else if (command == "add" && string.IsNullOrWhiteSpace(extra))
            args.Add("-A");   // stage everything when no path is given
        else if (!string.IsNullOrWhiteSpace(extra))
            args.AddRange(extraArgs);

        // GitHub auth (github.com only) is added by AriGit.Run.
        (int code, string outp, string err) = AriGit.Run(repoPath, args.ToArray());

        string combined = (outp + "\n" + err).Trim();
        if (string.IsNullOrWhiteSpace(combined))
            combined = command switch
            {
                "status" => "Working tree clean.",
                "fetch"  => "Already up to date.",
                "pull"   => "Already up to date.",
                "add"    => "Staged.",
                "push"   => "Pushed (nothing further to report).",
                _        => "(no output)"
            };

        return code != 0 ? $"git {command} exited {code}:\n{combined}" : combined;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    // Stages everything when nothing is staged yet (a deliberate partial stage is left alone), then commits
    // through AriGit so it's signed per the user's setting. "Committed <hash> <subject>" is the success
    // shape the memory agents key on.
    private static string Commit(string repoPath, string message, string extra)
    {
        if (string.IsNullOrWhiteSpace(message)) return "commit needs a 'message'.";

        (int stagedCode, string _, string _) = AriGit.Run(repoPath, ["diff", "--cached", "--quiet"]);
        if (stagedCode == 0) AriGit.Run(repoPath, ["add", "-A"]);
        (int anyStaged, string _, string _) = AriGit.Run(repoPath, ["diff", "--cached", "--quiet"]);
        bool amending = extra.Contains("--amend", StringComparison.Ordinal);
        if (anyStaged == 0 && !amending) return "Nothing to commit — working tree clean.";

        (int code, string outp, string err) = AriGit.Commit(repoPath, message, CommandSafety.Split(extra));
        if (code != 0) return $"git commit exited {code}:\n{(outp + "\n" + err).Trim()}";

        (int _, string head, string _) = AriGit.Run(repoPath, ["log", "-1", "--format=%h %s"]);
        return $"Committed {head}";
    }


    private static JsonElement Parse(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement; }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }

    private static string Str(JsonElement el, string prop, string fallback = "")
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback : fallback;
}

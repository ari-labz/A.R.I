using System.Diagnostics;
using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// Multi-repo git tool. Scans one level deep under the project root at construction time for
/// directories that contain a .git folder, then exposes them as a named enum. ARI never constructs
/// paths — she just picks a repo name and a command; the tool resolves the path itself.
/// </summary>
internal sealed class GitMulti : Tool
{
    private readonly Dictionary<string, string> _repos;  // display name → absolute path

    internal override string Name => "git";

    private GitMulti(Dictionary<string, string> repos) => _repos = repos;

    /// <summary>Scans projectRoot (one level deep) for subdirectories that contain a .git folder.
    /// Returns null if none are found so ToolFactories can skip registration cleanly.</summary>
    internal static GitMulti? Discover(string projectRoot)
    {
        if (!Directory.Exists(projectRoot)) return null;

        Dictionary<string, string> repos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string subdir in Directory.EnumerateDirectories(projectRoot))
        {
            string name = Path.GetFileName(subdir);
            if (name.StartsWith('.')) continue;
            if (Directory.Exists(Path.Combine(subdir, ".git")))
                repos[name] = subdir;
        }

        return repos.Count == 0 ? null : new GitMulti(repos);
    }

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "git",
            description = $"Run a git command against one of the project's repositories. "
                        + $"Repos: {string.Join(", ", _repos.Keys)}. "
                        + "fetch before pull to preview incoming changes. status before commit. "
                        + "stage with add, then commit with a message, then push.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    repo = new
                    {
                        type        = "string",
                        @enum       = _repos.Keys.Order().ToArray(),
                        description = "Which repository to target."
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
                        description = "Commit message. Only used by commit — passed as -m so spaces and quotes are safe."
                    },
                    args = new
                    {
                        type        = "string",
                        description = "Optional extra arguments (e.g. a file path for add/diff/log, a branch name for checkout, 'origin main' for push, '--oneline' for log). add with no args stages everything."
                    }
                },
                required = new[] { "repo", "command" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        JsonElement a = Parse(argsJson);
        string repo    = Str(a, "repo");
        string command = Str(a, "command");
        string extra   = Str(a, "args");

        if (!_repos.TryGetValue(repo, out string? repoPath))
            return Task.FromResult<ToolResult>($"Unknown repo '{repo}'. Available: {string.Join(", ", _repos.Keys)}");

        List<string> args = new List<string> { command };

        if (command == "commit")
        {
            // Message goes through as a single -m argument so spaces/quotes survive the arg split below.
            string message = Str(a, "message");
            if (!string.IsNullOrWhiteSpace(message)) args.AddRange(["-m", message]);
            else if (!string.IsNullOrWhiteSpace(extra)) args.AddRange(SplitArgs(extra));
        }
        else if (command == "log" && string.IsNullOrWhiteSpace(extra))
            args.AddRange(["-n15", "--oneline"]);
        else if (command == "add" && string.IsNullOrWhiteSpace(extra))
            args.Add("-A");   // stage everything when no path is given
        else if (!string.IsNullOrWhiteSpace(extra))
            args.AddRange(SplitArgs(extra));

        (int code, string outp, string err) = RunGit(repoPath, args.ToArray());

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

        return Task.FromResult<ToolResult>(code != 0 ? $"git {command} exited {code}:\n{combined}" : combined);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string[] SplitArgs(string extra)
        => extra.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (int Code, string Out, string Err) RunGit(string workDir, string[] args)
    {
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName               = "git",
            WorkingDirectory       = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using Process proc = Process.Start(psi)!;
        string outp = proc.StandardOutput.ReadToEnd();
        string err  = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, outp.Trim(), err.Trim());
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

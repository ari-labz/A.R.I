using System.Diagnostics;
using System.Text.Json;
using ARI.Common;

namespace ARI.LLM;

/// <summary>
/// GitHub, through GitHub's own CLI (gh) — issues, pull requests, repos, releases, Actions, and `gh api` for
/// anything else. A thin wrapper: ARI passes the arguments after `gh`, and this runs them as the account
/// connected in the control panel (GH_TOKEN), with gh's config kept inside ARI's folder and every prompt
/// off so nothing can hang. gh is provisioned by <see cref="GhCli"/>; the tool only exists once it's
/// installed, an account is connected, and the chat is the admin's own (see ToolFactories).
/// </summary>
internal sealed class GitHubTool : Tool
{
    private const int MAX_OUTPUT_CHARS = 6000;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly string  ghPath;
    private readonly string? workDir;   // the bound project, so repo-relative commands (`pr create`) find its repo
    private readonly Thread  thread;    // for approval prompts on destructive commands

    internal GitHubTool(string ghPath, string? workDir, Thread thread)
    {
        this.ghPath  = ghPath;
        this.workDir = workDir;
        this.thread  = thread;
    }

    internal override string Name => "github";

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "github",
            description = "Run a GitHub CLI (gh) command as the connected GitHub account: issues, pull requests, "
                        + "repos, releases, Actions runs, or `api` for anything else. Pass everything after 'gh', "
                        + "e.g. 'issue list -R owner/repo --state open' or 'pr view 12 -R owner/repo'. Use --json "
                        + "to get structured output. To put a repo on disk, use git_clone.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    command = new { type = "string", description = "The gh arguments, e.g. 'repo create my-repo --private'. Quote values with spaces." },
                    body    = new { type = "string", description = "Optional long text passed as --body (issue, PR or comment bodies) — newlines and quotes are kept as-is." }
                },
                required = new[] { "command" }
            }
        }
    };

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        JsonElement a = Parse(argsJson);
        List<string> args = CommandSafety.Split(Str(a, "command")).ToList();
        if (args.FirstOrDefault() == "gh") args.RemoveAt(0);
        if (args.Count == 0) return "Give a gh command, e.g. 'issue list -R owner/repo'.";

        if (CommandSafety.GhIsBlocked(args))
            return $"'gh {args[0]}' isn't available to ARI — it manages gh's own login, config or plugins. The GitHub connection is managed from the control panel's GitHub page.";
        if (CommandSafety.GhApprovalReason(args) is { } reason)
        {
            string shown = $"gh {string.Join(' ', args)}";
            if (!await ToolApprovals.RequestAsync(thread, $"ARI wants to run a GitHub command that {reason}.", shown))
                return $"Not run: this {reason}, and the user didn't approve it. If it's still needed, tell them the exact command so they can run it: {shown}";
        }

        if (Str(a, "body") is { Length: > 0 } body) args.AddRange(["--body", body]);

        string? token = GitHubStore.ResolveToken();
        if (token is null) return "No GitHub account is connected — ask the user to connect one on the control panel's GitHub page.";

        (int code, string outp, string err) = await RunGh(args, token);
        string combined = (outp + (err.Length > 0 ? "\n" + err : "")).Trim();
        if (combined.Length == 0) combined = "(no output)";
        if (combined.Length > MAX_OUTPUT_CHARS)
            combined = combined[..MAX_OUTPUT_CHARS] + $"\n… [truncated at {MAX_OUTPUT_CHARS} chars — narrow it with -L/--limit, --json fields or -q]";
        return code == 0 ? combined : $"gh exited {code}:\n{combined}";
    }

    private async Task<(int Code, string Out, string Err)> RunGh(IEnumerable<string> args, string token)
    {
        ProcessStartInfo psi = new()
        {
            FileName               = ghPath,
            WorkingDirectory       = workDir is { } w && Directory.Exists(w) ? w : Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.Environment["GH_TOKEN"]              = token;
        psi.Environment["GH_CONFIG_DIR"]         = GhCli.ConfigDir;   // never touch the user's ~/.config/gh
        psi.Environment["GH_PROMPT_DISABLED"]    = "1";
        psi.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        psi.Environment["GH_PAGER"]              = "cat";
        psi.Environment["NO_COLOR"]              = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"]   = "0";
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using Process proc = Process.Start(psi)!;
        Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> errTask = proc.StandardError.ReadToEndAsync();
        using CancellationTokenSource cts = new(Timeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, "", $"Timed out after {Timeout.TotalMinutes:0} minutes.");
        }
        return (proc.ExitCode, (await outTask).Trim(), (await errTask).Trim());
    }

    private static JsonElement Parse(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement; }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }

    private static string Str(JsonElement el, string prop, string fallback = "")
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
}

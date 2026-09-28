using System.Diagnostics;
using System.Text.Json;
using ARI.Common;

namespace ARI.LLM;

/// <summary>Clones a GitHub repo into the project root — the one git operation GitMulti can't cover, since
/// GitMulti only discovers repos that already exist on disk. Injects the connected GitHub token (if any) as
/// a per-invocation auth header, never written to .git/config, so private repos clone too.</summary>
internal sealed class GitClone : Tool
{
    private readonly string projectRoot;
    internal GitClone(string projectRoot) => this.projectRoot = projectRoot;

    internal override string Name => "git_clone";

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "git_clone",
            description = "Clone a GitHub repository into this project, as a new subfolder named after the "
                        + "repo. Works for public repos, and for private ones if a GitHub account is "
                        + "connected (control panel → Integrations → GitHub). Afterward, use the 'git' tool "
                        + "to pull, push, or commit against it.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    repo = new { type = "string", description = "Repository as 'owner/name', e.g. 'ari-labz/A.R.I'." }
                },
                required = new[] { "repo" }
            }
        }
    };

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        JsonElement a = Parse(argsJson);
        string repo = Str(a, "repo");
        if (repo.Split('/') is not [{ Length: > 0 } owner, { Length: > 0 } name])
            return "Give the repo as 'owner/name', e.g. 'ari-labz/A.R.I'.";

        string dest = Path.Combine(projectRoot, name);
        if (Directory.Exists(dest))
            return $"'{name}' already exists in this project.";

        List<string> args = new();
        if (GitHubStore.ResolveToken() is { Length: > 0 } token)
            args.AddRange(["-c", $"http.extraheader=AUTHORIZATION: bearer {token}"]);
        args.AddRange(["clone", $"https://github.com/{owner}/{name}.git", dest]);

        (int code, string outp, string err) = await RunGit(args.ToArray());
        if (code != 0)
            return $"git clone exited {code}:\n{Truncate((outp + "\n" + err).Trim(), 1500)}";

        return $"Cloned {owner}/{name} into {name}/. Use the git tool to pull, push, or commit against it.";
    }

    private static async Task<(int Code, string Out, string Err)> RunGit(string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName               = "git",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using Process proc = Process.Start(psi)!;
        string outp = await proc.StandardOutput.ReadToEndAsync();
        string err  = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, outp.Trim(), err.Trim());
    }

    private static JsonElement Parse(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement; }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }

    private static string Str(JsonElement el, string prop, string fallback = "")
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + $"\n… [truncated at {max} chars]";
}

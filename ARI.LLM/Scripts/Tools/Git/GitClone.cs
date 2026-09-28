using System.Text.Json;

namespace ARI.LLM;

/// <summary>Clones a repository from any host into the project — the one git operation GitMulti can't cover,
/// since GitMulti only discovers repos that already exist on disk. Takes a full clone URL (https or ssh, any
/// host) or 'owner/name' as shorthand for GitHub. A connected GitHub account authenticates github.com clones
/// (via AriGit.Run); other hosts use the machine's own git credentials.</summary>
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
            description = "Clone a git repository from any host into this project, as a new subfolder. Private "
                        + "GitHub repos work once a GitHub account is connected (control panel → GitHub); other "
                        + "hosts use this machine's own git logins. Afterward, use the 'git' tool on it.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    source = new { type = "string", description = "A clone URL (https://… or git@host:…) from any host, or 'owner/name' as shorthand for a GitHub repo." },
                    folder = new { type = "string", description = "Optional subfolder name. Defaults to the repository's name." }
                },
                required = new[] { "source" }
            }
        }
    };

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        JsonElement a = Parse(argsJson);
        string source = Str(a, "source").Trim();
        if (source.Length == 0) source = Str(a, "repo").Trim();   // older calls named it 'repo'
        if (source.Length == 0) return "Give a 'source': a clone URL, or 'owner/name' for GitHub.";

        bool isUrl = source.Contains("://") || source.Contains('@');
        if (!isUrl && source.Split('/') is not [{ Length: > 0 }, { Length: > 0 }])
            return "Give a full clone URL, or 'owner/name' for a GitHub repo (e.g. 'ari-labz/A.R.I').";
        string url = isUrl ? source : $"https://github.com/{source}.git";

        string folder = Str(a, "folder").Trim();
        if (folder.Length == 0)
        {
            string last = url.TrimEnd('/').Split('/', ':').Last();
            folder = last.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? last[..^4] : last;
        }
        string dest = Path.GetFullPath(Path.Combine(projectRoot, folder));
        if (!dest.StartsWith(Path.GetFullPath(projectRoot), StringComparison.OrdinalIgnoreCase))
            return "The folder must be inside this project.";
        if (Directory.Exists(dest))
            return $"'{folder}' already exists in this project.";

        // GitHub auth (github.com only) is added by AriGit.Run.
        (int code, string outp, string err) = await Task.Run(() => AriGit.Run(projectRoot, ["clone", url, dest]));
        if (code != 0)
            return $"git clone exited {code}:\n{Truncate((outp + "\n" + err).Trim(), 1500)}";

        return $"Cloned {url} into {folder}/. Use the git tool to pull, push, or commit against it.";
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

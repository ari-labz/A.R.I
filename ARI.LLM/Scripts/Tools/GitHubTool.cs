using System.Net;
using System.Text.Json;
using ARI.Common;

namespace ARI.LLM;

/// <summary>Reads GitHub over the REST API — no gh binary to provision. Uses a stored token when one is
/// available (project token first, then the user token), and calls unauthenticated otherwise. When a call
/// fails because the repo is private or not found, it tells the model to ask the user for a token in chat;
/// the set_token operation then stores what the user pastes, scoped to this project or the whole user.</summary>
internal sealed class GitHubTool : Tool
{
    private const string ApiBase = "https://api.github.com";
    private static readonly HttpClient Http = new();

    private readonly string? projectRoot;
    internal GitHubTool(string? projectRoot) => this.projectRoot = projectRoot;

    internal override string Name => "github";

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "github",
            description = "Read a GitHub repository over the REST API: view a repo, list or view issues and "
                        + "pull requests, or list recent Actions runs. If a repo is private and the call fails, "
                        + "ask the user for a GitHub token in chat, then call set_token to store it (scope "
                        + "'project' for just this project, or 'user' for everywhere) and retry.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    operation = new { type = "string", @enum = new[] { "repo", "issues", "issue", "prs", "pr", "actions", "set_token" }, description = "What to do." },
                    repo      = new { type = "string",  description = "Target repository as 'owner/name' (e.g. 'ari-labz/A.R.I'). Required for every operation except set_token." },
                    number    = new { type = "integer", description = "Issue or PR number, for the 'issue' and 'pr' operations." },
                    state     = new { type = "string",  @enum = new[] { "open", "closed", "all" }, description = "Filter for the 'issues' and 'prs' lists. Default open." },
                    token     = new { type = "string",  description = "A GitHub token the user gave you, for set_token only." },
                    scope     = new { type = "string",  @enum = new[] { "project", "user" }, description = "Where set_token stores the token: 'project' (this project only) or 'user' (everywhere)." }
                },
                required = new[] { "operation" }
            }
        }
    };

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        JsonElement a       = Parse(argsJson);
        string operation    = Str(a, "operation");
        string state        = Str(a, "state", "open");

        if (operation == "set_token")
            return StoreToken(Str(a, "token"), Str(a, "scope", "user"));

        string repo = Str(a, "repo");
        if (repo.Split('/') is not [{ Length: > 0 } owner, { Length: > 0 } name])
            return "Give the repo as 'owner/name', e.g. 'ari-labz/A.R.I'.";

        string path = operation switch
        {
            "repo"    => $"/repos/{owner}/{name}",
            "issues"  => $"/repos/{owner}/{name}/issues?state={state}&per_page=20",
            "issue"   => $"/repos/{owner}/{name}/issues/{Int(a, "number")}",
            "prs"     => $"/repos/{owner}/{name}/pulls?state={state}&per_page=20",
            "pr"      => $"/repos/{owner}/{name}/pulls/{Int(a, "number")}",
            "actions" => $"/repos/{owner}/{name}/actions/runs?per_page=15",
            _         => ""
        };
        if (path.Length == 0) return $"Unknown operation '{operation}'.";

        (HttpStatusCode code, string body) = await Get(path);

        if (code is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return $"GitHub returned {(int)code} for {owner}/{name}. It may be private or need authentication. "
                 + "If it's private, ask the user to paste a GitHub token (a fine-grained PAT with read access) "
                 + "in the chat, then call github with operation 'set_token' (scope 'project' or 'user') to store "
                 + "it and retry this call.";
        if (code != HttpStatusCode.OK)
            return $"GitHub returned {(int)code}: {Truncate(body, 300)}";

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return Format(operation, doc.RootElement);
        }
        catch { return Truncate(body, 2000); }
    }

    private ToolResult StoreToken(string token, string scope)
    {
        if (token.Length == 0) return "No token given. Pass the token the user shared as 'token'.";
        if (scope == "project")
        {
            if (projectRoot is not { Length: > 0 })
                return "No project is bound, so a project-scoped token can't be stored. Use scope 'user' instead.";
            GitHubStore.SetProjectToken(projectRoot, token);
            return "Stored the GitHub token for this project. Future github calls in this project will use it.";
        }
        GitHubStore.SetUserToken(token);
        return "Stored the GitHub token for your account. Future github calls will use it.";
    }

    private async Task<(HttpStatusCode, string)> Get(string path)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, ApiBase + path);
        req.Headers.UserAgent.ParseAdd("ARI");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (GitHubStore.Resolve(projectRoot) is { Length: > 0 } token)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        try
        {
            using HttpResponseMessage res = await Http.SendAsync(req);
            return (res.StatusCode, await res.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { return (HttpStatusCode.ServiceUnavailable, ex.Message); }
    }

    // ── Formatting ───────────────────────────────────────────────────────────────

    private static ToolResult Format(string operation, JsonElement root) => operation switch
    {
        "repo"           => FormatRepo(root),
        "issues" or "prs" => FormatList(root),
        "issue" or "pr"   => FormatItem(root),
        "actions"        => FormatRuns(root),
        _                => root.ToString()
    };

    private static string FormatRepo(JsonElement r)
        => $"{S(r, "full_name")} {(B(r, "private") ? "(private)" : "(public)")}\n"
         + $"{S(r, "description")}\n"
         + $"★ {I(r, "stargazers_count")}  ·  {I(r, "open_issues_count")} open issues  ·  default branch: {S(r, "default_branch")}";

    private static string FormatList(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return "None found.";
        List<string> lines = new();
        foreach (JsonElement e in arr.EnumerateArray())
            lines.Add($"#{I(e, "number")} [{S(e, "state")}] {S(e, "title")} — {S(e.GetProperty("user"), "login")}");
        return string.Join("\n", lines);
    }

    private static string FormatItem(JsonElement e)
    {
        string head = $"#{I(e, "number")} [{S(e, "state")}] {S(e, "title")}  by {S(e.GetProperty("user"), "login")}";
        if (e.TryGetProperty("head", out JsonElement h) && e.TryGetProperty("base", out JsonElement b))
            head += $"\n{S(h, "ref")} → {S(b, "ref")}{(B(e, "merged") ? "  (merged)" : "")}";
        return $"{head}\n\n{Truncate(S(e, "body"), 1500)}";
    }

    private static string FormatRuns(JsonElement root)
    {
        if (!root.TryGetProperty("workflow_runs", out JsonElement runs) || runs.GetArrayLength() == 0) return "No Actions runs.";
        List<string> lines = new();
        foreach (JsonElement e in runs.EnumerateArray())
            lines.Add($"{S(e, "name")} [{S(e, "status")}/{S(e, "conclusion")}] {S(e, "head_branch")} — {S(e, "created_at")}");
        return string.Join("\n", lines);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static JsonElement Parse(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement; }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }

    private static string Str(JsonElement el, string prop, string fallback = "")
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static int Int(JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.TryGetInt32(out int n) ? n : 0;

    private static string S(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int I(JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.TryGetInt32(out int n) ? n : 0;

    private static bool B(JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind is JsonValueKind.True;

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + $"\n… [truncated at {max} chars]";
}

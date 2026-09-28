using System.Net;
using System.Text.Json;
using ARI.Common;

namespace ARI.LLM;

/// <summary>Reads GitHub over the REST API — no gh binary to provision. Uses the token from GitHubStore when
/// one is connected, and calls unauthenticated otherwise. There is no chat-side way to set a token — the
/// connection is made once, from the control panel's GitHub page, via the OAuth device flow (see
/// GitHubDeviceAuth). When a call fails because the repo is private, this tool can only tell the model to
/// ask the user to connect (or reconnect) GitHub there.</summary>
internal sealed class GitHubTool : Tool
{
    private const string ApiBase = "https://api.github.com";
    private static readonly HttpClient Http = new();

    internal override string Name => "github";

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "github",
            description = "Work with GitHub over the REST API: view a repo, list or view issues and pull "
                        + "requests, list recent Actions runs, list repos you or an org have, create a new "
                        + "repo, or fork one. Reading public repos always works; private repos, creating, "
                        + "and forking all need a GitHub account connected in the control panel (Integrations "
                        + "→ GitHub) — there is no way to authenticate this from chat, so if a call fails "
                        + "because of that, tell the user to connect (or reconnect) it there. To bring a repo "
                        + "onto disk after finding/creating/forking it, use the git_clone tool.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    operation = new { type = "string", @enum = new[] { "repo", "issues", "issue", "prs", "pr", "actions", "list_repos", "list_org_repos", "create_repo", "fork" }, description = "What to do." },
                    repo      = new { type = "string",  description = "Target repository as 'owner/name' (e.g. 'ari-labz/A.R.I'). Required for repo/issues/issue/prs/pr/actions/fork." },
                    number    = new { type = "integer", description = "Issue or PR number, for the 'issue' and 'pr' operations." },
                    state     = new { type = "string",  @enum = new[] { "open", "closed", "all" }, description = "Filter for the 'issues' and 'prs' lists. Default open." },
                    org       = new { type = "string",  description = "An organisation login. Required for list_org_repos. Optional for create_repo/fork, to create/fork into that org instead of the connected user's own account." },
                    name      = new { type = "string",  description = "New repo name, for create_repo only." },
                    @private  = new { type = "boolean", description = "For create_repo only. Defaults to true — pass false to create it public." },
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

        switch (operation)
        {
            case "create_repo":     return await CreateRepo(a);
            case "fork":            return await Fork(a);
            case "list_repos":      return await ListMyRepos();
            case "list_org_repos":  return await ListOrgRepos(a);
        }

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
        if (code != HttpStatusCode.OK) return ErrorMessage(code, body, $"for {owner}/{name}");

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return Format(operation, doc.RootElement);
        }
        catch { return Truncate(body, 2000); }
    }

    // ── Write operations ────────────────────────────────────────────────────────

    private async Task<ToolResult> CreateRepo(JsonElement a)
    {
        string name = Str(a, "name");
        if (name.Length == 0) return "Give a repo 'name'.";
        bool priv = Bool(a, "private", true);
        string org = Str(a, "org");

        string path = org.Length > 0 ? $"/orgs/{org}/repos" : "/user/repos";
        string body = JsonSerializer.Serialize(new { name, @private = priv });

        (HttpStatusCode code, string res) = await Post(path, body);
        if (code != HttpStatusCode.Created) return ErrorMessage(code, res, "creating the repo");

        using JsonDocument doc = JsonDocument.Parse(res);
        JsonElement r = doc.RootElement;
        return $"Created {S(r, "full_name")} ({(priv ? "private" : "public")}): {S(r, "html_url")}. "
             + "Use git_clone to bring it onto disk.";
    }

    private async Task<ToolResult> Fork(JsonElement a)
    {
        string repo = Str(a, "repo");
        if (repo.Split('/') is not [{ Length: > 0 } owner, { Length: > 0 } name])
            return "Give the repo as 'owner/name', e.g. 'ari-labz/A.R.I'.";
        string org = Str(a, "org");
        string body = org.Length > 0 ? JsonSerializer.Serialize(new { organization = org }) : "{}";

        (HttpStatusCode code, string res) = await Post($"/repos/{owner}/{name}/forks", body);
        if (code is not (HttpStatusCode.Accepted or HttpStatusCode.Created)) return ErrorMessage(code, res, "forking the repo");

        using JsonDocument doc = JsonDocument.Parse(res);
        JsonElement r = doc.RootElement;
        return $"Forked to {S(r, "full_name")}: {S(r, "html_url")}. GitHub can take a few seconds to finish "
             + "setting it up before git_clone will work on it.";
    }

    private async Task<ToolResult> ListMyRepos()
    {
        if (GitHubStore.ResolveToken() is null)
            return "No GitHub account is connected — connect one in the control panel (Integrations → GitHub) to list repos you have access to.";
        (HttpStatusCode code, string body) = await Get("/user/repos?per_page=50&sort=updated&affiliation=owner,collaborator,organization_member");
        if (code != HttpStatusCode.OK) return ErrorMessage(code, body, "listing your repos");
        using JsonDocument doc = JsonDocument.Parse(body);
        return FormatRepoList(doc.RootElement);
    }

    private async Task<ToolResult> ListOrgRepos(JsonElement a)
    {
        string org = Str(a, "org");
        if (org.Length == 0) return "Give an 'org' name.";
        (HttpStatusCode code, string body) = await Get($"/orgs/{org}/repos?per_page=50&sort=updated");
        if (code != HttpStatusCode.OK) return ErrorMessage(code, body, $"listing repos for {org}");
        using JsonDocument doc = JsonDocument.Parse(body);
        return FormatRepoList(doc.RootElement);
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────────

    private async Task<(HttpStatusCode, string)> Get(string path)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, ApiBase + path);
        req.Headers.UserAgent.ParseAdd("ARI");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (GitHubStore.ResolveToken() is { Length: > 0 } token)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        try
        {
            using HttpResponseMessage res = await Http.SendAsync(req);
            return (res.StatusCode, await res.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { return (HttpStatusCode.ServiceUnavailable, ex.Message); }
    }

    /// <summary>Every write operation needs a connected account — there's no such thing as an anonymous
    /// create/fork — so this fails fast with the same "connect GitHub" message rather than calling out.</summary>
    private async Task<(HttpStatusCode, string)> Post(string path, string jsonBody)
    {
        if (GitHubStore.ResolveToken() is not { Length: > 0 } token)
            return (HttpStatusCode.Unauthorized, "");

        using HttpRequestMessage req = new(HttpMethod.Post, ApiBase + path);
        req.Headers.UserAgent.ParseAdd("ARI");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

        try
        {
            using HttpResponseMessage res = await Http.SendAsync(req);
            return (res.StatusCode, await res.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { return (HttpStatusCode.ServiceUnavailable, ex.Message); }
    }

    private static string ErrorMessage(HttpStatusCode code, string body, string context)
    {
        if (code is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return $"GitHub returned {(int)code} {context}. It may be private, need a permission the "
                 + "connected account doesn't have, or need a GitHub account connected at all — tell the "
                 + "user to connect (or reconnect) GitHub from the control panel (Integrations → GitHub). "
                 + "There is no way to authenticate this from chat.";
        return $"GitHub returned {(int)code} {context}: {Truncate(body, 300)}";
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

    private static string FormatRepoList(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return "None found.";
        List<string> lines = new();
        foreach (JsonElement e in arr.EnumerateArray())
            lines.Add($"{S(e, "full_name")} {(B(e, "private") ? "(private)" : "(public)")} — {S(e, "description")}");
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

    private static bool Bool(JsonElement el, string prop, bool fallback)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + $"\n… [truncated at {max} chars]";
}

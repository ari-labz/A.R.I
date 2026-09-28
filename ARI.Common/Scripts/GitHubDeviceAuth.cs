using System.Text.Json;

namespace ARI.Common;

public enum GitHubConnectState { Idle, Pending, Connected, Expired, Denied, Error }

public sealed record GitHubConnectStatus(
    GitHubConnectState State,
    string? UserCode = null,
    string? VerificationUri = null,
    string? Login = null,
    string? Error = null);

/// <summary>
/// Drives the GitHub OAuth device-authorization flow (RFC 8628) so ARI can be connected to a GitHub
/// account without anyone ever typing or pasting a token: the control panel shows a short code, you
/// approve it at github.com/login/device, and whatever comes back lands in GitHubStore.
///
/// One flow runs at a time — starting a new one cancels whatever was still pending. State lives in a
/// static field because there is exactly one GitHub connection for the whole ARI instance, the same way
/// GitHubStore holds one settings file rather than per-thread state.
/// </summary>
public static class GitHubDeviceAuth
{
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string TokenUrl      = "https://github.com/login/oauth/access_token";

    private static readonly HttpClient Http = new();
    private static readonly object Lock = new();
    private static GitHubConnectStatus _status = new(GitHubConnectState.Idle);
    private static CancellationTokenSource? _pollCts;

    public static GitHubConnectStatus Status { get { lock (Lock) return _status; } }

    /// <summary>Requests a device code and starts polling for approval in the background. Returns as soon
    /// as the code is issued — the caller doesn't wait for the user to approve it.</summary>
    public static async Task<GitHubConnectStatus> StartAsync(string clientId, string scope = "repo read:org")
    {
        _pollCts?.Cancel();
        CancellationTokenSource cts = _pollCts = new CancellationTokenSource();

        Dictionary<string, string> form = new() { ["client_id"] = clientId, ["scope"] = scope };
        string body;
        try
        {
            using HttpResponseMessage res = await PostFormAsync(DeviceCodeUrl, form);
            body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                return SetStatus(new GitHubConnectStatus(GitHubConnectState.Error, Error: $"GitHub returned {(int)res.StatusCode} starting the connection."));
        }
        catch (Exception ex) { return SetStatus(new GitHubConnectStatus(GitHubConnectState.Error, Error: ex.Message)); }

        JsonElement root;
        try { using JsonDocument doc = JsonDocument.Parse(body); root = doc.RootElement.Clone(); }
        catch { return SetStatus(new GitHubConnectStatus(GitHubConnectState.Error, Error: "GitHub sent back something unexpected.")); }

        if (!root.TryGetProperty("device_code", out JsonElement dc) || !root.TryGetProperty("user_code", out JsonElement uc)
            || !root.TryGetProperty("verification_uri", out JsonElement vu))
        {
            // GitHub reports a bad client_id, device flow not enabled, etc. as a 200 with an {error,
            // error_description} body rather than a non-2xx status — surface that instead of guessing.
            string ghError = root.TryGetProperty("error_description", out JsonElement ed) ? ed.GetString() ?? ""
                           : root.TryGetProperty("error", out JsonElement er) ? er.GetString() ?? "" : "";
            return SetStatus(new GitHubConnectStatus(GitHubConnectState.Error,
                Error: ghError.Length > 0 ? ghError : "GitHub didn't return a device code — check the Client ID."));
        }

        string deviceCode = dc.GetString() ?? "";
        int expiresIn = root.TryGetProperty("expires_in", out JsonElement ei) ? ei.GetInt32() : 900;
        int interval  = root.TryGetProperty("interval", out JsonElement iv) ? iv.GetInt32() : 5;

        GitHubConnectStatus status = SetStatus(new GitHubConnectStatus(
            GitHubConnectState.Pending, UserCode: uc.GetString(), VerificationUri: vu.GetString()));

        _ = PollAsync(clientId, deviceCode, interval, expiresIn, cts.Token);
        return status;
    }

    private static async Task PollAsync(string clientId, string deviceCode, int interval, int expiresIn, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(expiresIn);

        while (DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(interval), ct); }
            catch (OperationCanceledException) { return; }

            Dictionary<string, string> form = new()
            {
                ["client_id"]   = clientId,
                ["device_code"] = deviceCode,
                ["grant_type"]  = "urn:ietf:params:oauth:grant-type:device_code",
            };

            string body;
            try
            {
                using HttpResponseMessage res = await PostFormAsync(TokenUrl, form, ct);
                body = await res.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { SetStatus(new GitHubConnectStatus(GitHubConnectState.Error, Error: ex.Message)); return; }

            JsonElement root;
            try { using JsonDocument doc = JsonDocument.Parse(body); root = doc.RootElement.Clone(); }
            catch { continue; }

            if (root.TryGetProperty("access_token", out JsonElement tok) && tok.GetString() is { Length: > 0 } token)
            {
                string login = await FetchLoginAsync(token, ct);
                GitHubStore.Set(new GitHubSettings { ClientId = clientId, AccessToken = token, Login = login });
                SetStatus(new GitHubConnectStatus(GitHubConnectState.Connected, Login: login));
                return;
            }

            string error = root.TryGetProperty("error", out JsonElement e) ? e.GetString() ?? "" : "";
            switch (error)
            {
                case "authorization_pending": continue;
                case "slow_down": interval += 5; continue;
                case "expired_token": SetStatus(new GitHubConnectStatus(GitHubConnectState.Expired)); return;
                case "access_denied": SetStatus(new GitHubConnectStatus(GitHubConnectState.Denied)); return;
                default: SetStatus(new GitHubConnectStatus(GitHubConnectState.Error, Error: error.Length > 0 ? error : "Unknown error.")); return;
            }
        }

        SetStatus(new GitHubConnectStatus(GitHubConnectState.Expired));
    }

    private static async Task<string> FetchLoginAsync(string token, CancellationToken ct)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, "https://api.github.com/user");
        req.Headers.UserAgent.ParseAdd("ARI");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        try
        {
            using HttpResponseMessage res = await Http.SendAsync(req, ct);
            using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("login", out JsonElement l) ? l.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private static GitHubConnectStatus SetStatus(GitHubConnectStatus s) { lock (Lock) { _status = s; return s; } }

    // GitHub's device/token endpoints reply with form-urlencoded text, not JSON, unless the request
    // explicitly asks for JSON — Http.PostAsync's convenience overload has no way to set that header.
    private static Task<HttpResponseMessage> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct = default)
    {
        HttpRequestMessage req = new(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Accept.ParseAdd("application/json");
        return Http.SendAsync(req, ct);
    }
}

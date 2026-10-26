using ARI.Common;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>Control-panel endpoints for connecting ARI to a GitHub account via the OAuth device-authorization
/// flow — nobody types or pastes a token here; you approve a short code on github.com and whatever comes
/// back is stored server-side. See GitHubDeviceAuth for the flow itself.</summary>
[Route("api/v1/admin/integrations/github")]
[ApiController]
public class GitHubController : ControllerBase
{
    private const string Scope = "repo read:org";

    /// <summary>Current connection: the Client ID (not secret), and whether an account is connected. The
    /// access token itself is never returned.</summary>
    [HttpGet]
    public IActionResult GetConfig()
    {
        GitHubSettings s = GitHubStore.Get();
        return Ok(new
        {
            clientId  = s.ClientId,
            connected = !string.IsNullOrWhiteSpace(s.AccessToken),
            login     = s.Login,
            commitAs  = s.CommitAs.ToString(),
        });
    }

    /// <summary>Changes settings; send only what changes. <c>commitAs</c> is how ARI signs the commits she makes
    /// (Author or CoAuthor), for every repo, connected or not. <c>clientId</c> is the OAuth App's Client ID, the one
    /// manual setup step, done once per app registration.</summary>
    [HttpPatch]
    public IActionResult Change([FromBody] GitHubChange req)
    {
        CommitIdentity? identity = null;
        if (req.CommitAs is not null)
        {
            if (!Enum.TryParse(req.CommitAs, ignoreCase: true, out CommitIdentity parsed))
                return BadRequest(new { error = "commitAs must be Author or CoAuthor." });
            identity = parsed;
        }
        if (req.ClientId is not null && string.IsNullOrWhiteSpace(req.ClientId))
            return BadRequest(new { error = "Client ID is required." });

        GitHubSettings s = GitHubStore.Get();
        if (identity is { } who) s.CommitAs = who;
        if (req.ClientId is not null) s.ClientId = req.ClientId.Trim();
        GitHubStore.Set(s);
        return Ok(new { ok = true });
    }

    /// <summary>Starts the device flow: returns a short code and the URL to approve it at. ARI polls GitHub
    /// in the background — the panel polls connect/status until it lands.</summary>
    [HttpPost("connection")]
    public async Task<IActionResult> Connect()
    {
        string clientId = GitHubStore.Get().ClientId;
        if (string.IsNullOrWhiteSpace(clientId)) return BadRequest(new { error = "Save a Client ID first." });

        GitHubConnectStatus status = await GitHubDeviceAuth.StartAsync(clientId, Scope);
        if (status.State == GitHubConnectState.Error)
            return BadRequest(new { error = status.Error ?? "Failed to start the connection." });

        return Ok(new { userCode = status.UserCode, verificationUri = status.VerificationUri });
    }

    /// <summary>Polled by the panel while a connect is in progress.</summary>
    [HttpGet("connection")]
    public IActionResult ConnectStatus()
    {
        GitHubConnectStatus s = GitHubDeviceAuth.Status;
        return Ok(new { state = s.State.ToString().ToLowerInvariant(), login = s.Login, error = s.Error });
    }

    /// <summary>Drops the stored access token. The Client ID is kept — that's app registration, not a
    /// per-connection secret — so reconnecting doesn't need it re-entered.</summary>
    [HttpDelete("connection")]
    public IActionResult Disconnect()
    {
        GitHubStore.Disconnect();
        return Ok(new { ok = true });
    }
}

public sealed class GitHubChange
{
    public string? ClientId { get; set; }
    public string? CommitAs { get; set; }
}

public sealed class GitHubCommitAsRequest
{
    public string? CommitAs { get; set; }
}

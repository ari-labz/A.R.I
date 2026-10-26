using System.Security.Claims;
using ARI.API.Auth;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>
/// Admin-only endpoints for user management and the IP blocklist.
/// All routes require the caller to have the Admin role (enforced by the /api/v1/admin/ path rule in AuthMiddleware).
/// </summary>
[Route("api/v1/admin/users")]
[ApiController]
public class AdminUsersController(UserStore users) : ControllerBase
{
    // ── User list ─────────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult ListUsers()
    {
        return Ok(this.Page(users.GetAll().Select(u => new
        {
            u.Id,
            u.Username,
            u.Role,
            u.DisplayName,
            u.MustChangePassword,
            IsMaster     = u.Id.ToString() == ARI.Common.Shared.MasterUserId,
            CreatedAt    = DateTimeOffset.FromUnixTimeSeconds(u.CreatedAt).ToString("o"),
            LastActiveAt = u.LastActiveAt == 0 ? null : (string?)DateTimeOffset.FromUnixTimeSeconds(u.LastActiveAt).ToString("o"),
        }).ToList()));
    }

    // ── Create user ───────────────────────────────────────────────────────────────

    public record CreateUserRequest(string Username, string Password, string Role);

    [HttpPost]
    public IActionResult CreateUser([FromBody] CreateUserRequest req)
    {
        string role = req.Role is Roles.Admin or Roles.Guest ? req.Role : Roles.Guest;

        string username = (req.Username ?? "").Trim();
        if (AccountRules.CheckUsername(username) is { } usernameProblem)
            return BadRequest(new { error = usernameProblem });
        if (AccountRules.CheckPassword(req.Password) is { } passwordProblem)
            return BadRequest(new { error = passwordProblem });
        if (users.GetByUsername(username) is not null || users.GetAll().Any(u => u.DisplayName.Equals(username, StringComparison.OrdinalIgnoreCase)))
            return Conflict(new { error = "A user with that username already exists." });

        string hash = AuthService.HashPassword(req.Password);
        User   user = users.CreateUser(username, hash, role, displayName: username);
        return Ok(new { user.Id, user.Username, user.Role });
    }

    // ── Change a person's account (today: an admin sets a new temporary password) ──

    public record ChangeUserRequest(string? NewPassword);

    [HttpPatch("{id:int}")]
    public IActionResult ChangeUser(int id, [FromBody] ChangeUserRequest req)
    {
        User? user = users.GetById(id);
        if (user is null) return NotFound();
        // Resetting the Master's password would hand over the account that owns the brain, dreams and Discord link.
        if (id.ToString() == ARI.Common.Shared.MasterUserId && User.FindFirstValue(ClaimTypes.NameIdentifier) != id.ToString())
            return BadRequest(new { error = "Only the Master can change the Master's password." });

        if (req.NewPassword is null)
            return BadRequest(new { error = "Nothing to change." });
        if (AccountRules.CheckPassword(req.NewPassword) is { } passwordProblem)
            return BadRequest(new { error = passwordProblem });

        string hash = AuthService.HashPassword(req.NewPassword);
        users.SetPassword(id, hash, mustChange: true);
        users.RevokeAllSessionsForUser(id);
        return Ok();
    }

    // ── Revoke user ───────────────────────────────────────────────────────────────

    [HttpDelete("{id:int}")]
    public IActionResult DeleteUser(int id)
    {
        string? myIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (int.TryParse(myIdStr, out int myId) && myId == id)
            return BadRequest(new { error = "You cannot delete your own account." });

        User? user = users.GetById(id);
        if (user is null) return NotFound(new { error = "No such account." });
        if (id.ToString() == ARI.Common.Shared.MasterUserId)
            return BadRequest(new { error = "The Master account cannot be deleted." });

        users.RevokeAllSessionsForUser(id);
        users.DeleteUser(id);
        return Ok();
    }

    // ── Sessions (admin view) ─────────────────────────────────────────────────────

    [HttpGet("/api/v1/admin/sessions")]
    public IActionResult AllSessions()
    {
        return Ok(this.Page(users.GetAllActiveSessions().Select(s => new
        {
            s.SessionId,
            s.UserId,
            s.DeviceHint,
            s.IsDesktop,
            LastUsedAt = DateTimeOffset.FromUnixTimeSeconds(s.LastUsedAt).ToString("o"),
            ExpiresAt  = DateTimeOffset.FromUnixTimeSeconds(s.ExpiresAt).ToString("o"),
        }).ToList()));
    }

    [HttpDelete("/api/v1/admin/sessions/{id}")]
    public IActionResult RevokeSession(string id)
    {
        UserSession? session = users.GetSession(id);
        if (session is null) return NotFound(new { error = "No such session." });
        // Only the Master may sign the Master out.
        if (session.UserId.ToString() == ARI.Common.Shared.MasterUserId && User.FindFirstValue(ClaimTypes.NameIdentifier) != session.UserId.ToString())
            return BadRequest(new { error = "Only the Master can end the Master's sessions." });
        users.RevokeSession(id);
        return Ok();
    }

    // ── Personal access tokens (admin view) ───────────────────────────────────────

    /// <summary>Every token anyone has made (never the token itself), so an admin can see what has access and cut it off.</summary>
    [HttpGet("/api/v1/admin/tokens")]
    public IActionResult AllTokens()
    {
        Dictionary<int, string> names = users.GetAll().ToDictionary(u => u.Id, u => u.Username);
        return Ok(this.Page(users.ListTokens().Select(t => new
        {
            t.Id, t.Name, t.Prefix, t.UserId,
            Username   = names.TryGetValue(t.UserId, out string? n) ? n : "unknown",
            CreatedAt  = DateTimeOffset.FromUnixTimeSeconds(t.CreatedAt).ToString("o"),
            LastUsedAt = t.LastUsedAt == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(t.LastUsedAt).ToString("o"),
        }).ToList()));
    }

    [HttpDelete("/api/v1/admin/tokens/{id}")]
    public IActionResult RevokeToken(string id)
        => users.RevokeToken(id) ? Ok() : NotFound(new { error = "No such token." });

    // ── IP Blocklist ──────────────────────────────────────────────────────────────

    [HttpGet("/api/v1/admin/blocklist")]
    public IActionResult GetBlocklist()
    {
        return Ok(this.Page(users.GetBlockedIps().Select(b => new
        {
            b.Ip,
            BlockedAt      = DateTimeOffset.FromUnixTimeSeconds(b.BlockedAt).ToString("o"),
            b.FailedAttempts,
        }).ToList()));
    }

    [HttpDelete("/api/v1/admin/blocklist/{id}")]
    public IActionResult UnblockIp(string id)
    {
        return users.UnblockIp(id) ? Ok() : NotFound(new { error = "That address is not blocked." });
    }
}

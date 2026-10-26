using System.Security.Claims;
using ARI.API.Auth;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>
/// Admin-only endpoints for user management and the IP blocklist.
/// All routes require the caller to have the Admin role (enforced by AdminGuard middleware).
/// </summary>
[Route("api/v1/admin/users")]
[ApiController]
public class AdminUsersController(UserStore users) : ControllerBase
{
    // ── User list ─────────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult ListUsers()
    {
        if (!IsAdmin()) return StatusCode(403);
        return Ok(users.GetAll().Select(u => new
        {
            u.Id,
            u.Username,
            u.Role,
            u.DisplayName,
            u.MustChangePassword,
            IsMaster     = u.Id.ToString() == ARI.Common.Shared.MasterUserId,
            CreatedAt    = DateTimeOffset.FromUnixTimeSeconds(u.CreatedAt).ToString("o"),
            LastActiveAt = u.LastActiveAt == 0 ? null : (string?)DateTimeOffset.FromUnixTimeSeconds(u.LastActiveAt).ToString("o"),
        }));
    }

    // ── Create user ───────────────────────────────────────────────────────────────

    public record CreateUserRequest(string Username, string Password, string Role);

    [HttpPost]
    public IActionResult CreateUser([FromBody] CreateUserRequest req)
    {
        if (!IsAdmin()) return StatusCode(403);

        string role = req.Role is Roles.Admin or Roles.Guest ? req.Role : Roles.Guest;

        string username = (req.Username ?? "").Trim();
        if (AccountRules.CheckUsername(username) is { } usernameProblem)
            return BadRequest(new { error = usernameProblem });
        if (AccountRules.CheckPassword(req.Password) is { } passwordProblem)
            return BadRequest(new { error = passwordProblem });
        if (users.GetByUsername(username) is not null)
            return Conflict(new { error = "A user with that username already exists." });

        string hash = AuthService.HashPassword(req.Password);
        User   user = users.CreateUser(username, hash, role, displayName: username);
        return Ok(new { user.Id, user.Username, user.Role });
    }

    // ── Reset password (admin sets a new temp password) ───────────────────────────

    public record ResetPasswordRequest(string NewPassword);

    [HttpPost("{id:int}/reset-password")]
    public IActionResult ResetPassword(int id, [FromBody] ResetPasswordRequest req)
    {
        if (!IsAdmin()) return StatusCode(403);

        User? user = users.GetById(id);
        if (user is null) return NotFound();
        // Resetting the Master's password would hand over the account that owns the brain, dreams and Discord link.
        if (id.ToString() == ARI.Common.Shared.MasterUserId && User.FindFirstValue(ClaimTypes.NameIdentifier) != id.ToString())
            return BadRequest(new { error = "Only the Master can change the Master's password." });

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
        if (!IsAdmin()) return StatusCode(403);

        string? myIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (int.TryParse(myIdStr, out int myId) && myId == id)
            return BadRequest(new { error = "You cannot delete your own account." });

        User? user = users.GetById(id);
        if (user is null) return NotFound();
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
        if (!IsAdmin()) return StatusCode(403);
        return Ok(users.GetAllActiveSessions().Select(s => new
        {
            s.SessionId,
            s.UserId,
            s.DeviceHint,
            s.IsDesktop,
            LastUsedAt = DateTimeOffset.FromUnixTimeSeconds(s.LastUsedAt).ToString("o"),
            ExpiresAt  = DateTimeOffset.FromUnixTimeSeconds(s.ExpiresAt).ToString("o"),
        }));
    }

    [HttpDelete("/api/v1/admin/sessions/{sessionId}")]
    public IActionResult RevokeSession(string sessionId)
    {
        if (!IsAdmin()) return StatusCode(403);
        users.RevokeSession(sessionId);
        return Ok();
    }

    // ── IP Blocklist ──────────────────────────────────────────────────────────────

    [HttpGet("/api/v1/admin/blocklist")]
    public IActionResult GetBlocklist()
    {
        if (!IsAdmin()) return StatusCode(403);
        return Ok(users.GetBlockedIps().Select(b => new
        {
            b.Ip,
            BlockedAt      = DateTimeOffset.FromUnixTimeSeconds(b.BlockedAt).ToString("o"),
            b.FailedAttempts,
        }));
    }

    [HttpDelete("/api/v1/admin/blocklist/{ip}")]
    public IActionResult UnblockIp(string ip)
    {
        if (!IsAdmin()) return StatusCode(403);
        users.UnblockIp(ip);
        return Ok();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private bool IsAdmin() =>
        User.FindFirstValue(ClaimTypes.Role) == Roles.Admin;
}

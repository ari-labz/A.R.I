using System.Security.Claims;
using ARI.API.Auth;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>
/// Admin-only endpoints for user management and the IP blocklist.
/// All routes require the caller to have the Admin role (enforced by AdminGuard middleware).
/// </summary>
[Route("admin/users")]
[ApiController]
public class AdminUsersController(UserStore users) : ControllerBase
{
    // ── User list ─────────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult ListUsers()
    {
        if (!IsAdmin()) return Forbid();
        return Ok(users.GetAll().Select(u => new
        {
            u.Id,
            u.Username,
            u.Role,
            u.DisplayName,
            u.MustChangePassword,
            CreatedAt    = DateTimeOffset.FromUnixTimeSeconds(u.CreatedAt).ToString("o"),
            LastActiveAt = u.LastActiveAt == 0 ? null : (string?)DateTimeOffset.FromUnixTimeSeconds(u.LastActiveAt).ToString("o"),
        }));
    }

    // ── Create user ───────────────────────────────────────────────────────────────

    public record CreateUserRequest(string Username, string Password, string Role);

    [HttpPost]
    public IActionResult CreateUser([FromBody] CreateUserRequest req)
    {
        if (!IsAdmin()) return Forbid();

        string role = req.Role is Roles.Admin or Roles.Guest ? req.Role : Roles.Guest;

        if (string.IsNullOrWhiteSpace(req.Username))
            return BadRequest(new { error = "Username is required." });
        if (req.Password.Length < 6)
            return BadRequest(new { error = "Password must be at least 6 characters." });
        if (users.GetByUsername(req.Username) is not null)
            return Conflict(new { error = "A user with that username already exists." });

        string hash = AuthService.HashPassword(req.Password);
        User   user = users.CreateUser(req.Username, hash, role, displayName: req.Username);
        return Ok(new { user.Id, user.Username, user.Role });
    }

    // ── Reset password (admin sets a new temp password) ───────────────────────────

    public record ResetPasswordRequest(string NewPassword);

    [HttpPost("{id:int}/reset-password")]
    public IActionResult ResetPassword(int id, [FromBody] ResetPasswordRequest req)
    {
        if (!IsAdmin()) return Forbid();

        User? user = users.GetById(id);
        if (user is null) return NotFound();

        if (req.NewPassword.Length < 6)
            return BadRequest(new { error = "Password must be at least 6 characters." });

        string hash = AuthService.HashPassword(req.NewPassword);
        users.SetPassword(id, hash, mustChange: true);
        users.RevokeAllSessionsForUser(id);
        return Ok();
    }

    // ── Revoke user ───────────────────────────────────────────────────────────────

    [HttpDelete("{id:int}")]
    public IActionResult DeleteUser(int id)
    {
        if (!IsAdmin()) return Forbid();

        string? myIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (int.TryParse(myIdStr, out int myId) && myId == id)
            return BadRequest(new { error = "You cannot delete your own account." });

        User? user = users.GetById(id);
        if (user is null) return NotFound();

        users.RevokeAllSessionsForUser(id);
        users.DeleteUser(id);
        return Ok();
    }

    // ── Sessions (admin view) ─────────────────────────────────────────────────────

    [HttpGet("/admin/sessions")]
    public IActionResult AllSessions()
    {
        if (!IsAdmin()) return Forbid();
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

    [HttpDelete("/admin/sessions/{sessionId}")]
    public IActionResult RevokeSession(string sessionId)
    {
        if (!IsAdmin()) return Forbid();
        users.RevokeSession(sessionId);
        return Ok();
    }

    // ── IP Blocklist ──────────────────────────────────────────────────────────────

    [HttpGet("/admin/blocklist")]
    public IActionResult GetBlocklist()
    {
        if (!IsAdmin()) return Forbid();
        return Ok(users.GetBlockedIps().Select(b => new
        {
            b.Ip,
            BlockedAt      = DateTimeOffset.FromUnixTimeSeconds(b.BlockedAt).ToString("o"),
            b.FailedAttempts,
        }));
    }

    [HttpDelete("/admin/blocklist/{ip}")]
    public IActionResult UnblockIp(string ip)
    {
        if (!IsAdmin()) return Forbid();
        users.UnblockIp(ip);
        return Ok();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private bool IsAdmin() =>
        User.FindFirstValue(ClaimTypes.Role) == Roles.Admin;
}

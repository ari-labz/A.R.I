using System.Security.Claims;
using ARI.API.Auth;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>Preferences and session management for the currently signed-in user.</summary>
[Route("user")]
[ApiController]
public class UserController(UserStore users) : ControllerBase
{
    // ── Preferences ──────────────────────────────────────────────────────────────

    [HttpGet("preferences")]
    public IActionResult GetPreferences()
    {
        if (!TryGetUserId(out int id)) return Unauthorized();
        User? user = users.GetById(id);
        if (user is null) return Unauthorized();

        return Ok(new
        {
            user.DisplayName,
            Sessions = users.GetSessionsForUser(id).Select(s => new
            {
                s.SessionId,
                s.DeviceHint,
                s.IsDesktop,
                LastUsedAt = DateTimeOffset.FromUnixTimeSeconds(s.LastUsedAt).ToString("o"),
                ExpiresAt  = DateTimeOffset.FromUnixTimeSeconds(s.ExpiresAt).ToString("o"),
            }),
        });
    }

    public record PrefsUpdate(string DisplayName);

    [HttpPut("preferences")]
    public IActionResult UpdatePreferences([FromBody] PrefsUpdate req)
    {
        if (!TryGetUserId(out int id)) return Unauthorized();
        string name = req.DisplayName.Trim();
        if (name.Length == 0) return BadRequest(new { error = "Display name cannot be empty." });
        users.SetDisplayName(id, name);
        return Ok();
    }

    public record UsernameUpdate(string NewUsername, string CurrentPassword);

    [HttpPut("username")]
    public IActionResult UpdateUsername([FromBody] UsernameUpdate req)
    {
        if (!TryGetUserId(out int id)) return Unauthorized();
        User? user = users.GetById(id);
        if (user is null) return Unauthorized();

        if (!AuthService.VerifyPassword(req.CurrentPassword, user.PasswordHash))
            return BadRequest(new { error = "Current password is incorrect." });

        string name = req.NewUsername.Trim();
        if (name.Length < 2)   return BadRequest(new { error = "Username must be at least 2 characters." });
        if (name.Length > 40)  return BadRequest(new { error = "Username must be 40 characters or fewer." });
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9_\-\.]+$"))
            return BadRequest(new { error = "Username may only contain letters, numbers, underscores, hyphens, and dots." });

        if (!users.SetUsername(id, name))
            return Conflict(new { error = "That username is already taken." });

        // The JWT carries the old username — log out all sessions so the next login picks up the new name.
        users.RevokeAllSessionsForUser(id);
        return Ok(new { message = "Username updated. Please sign in again." });
    }

    // ── Sessions ─────────────────────────────────────────────────────────────────

    [HttpDelete("sessions/{sessionId}")]
    public IActionResult RevokeSession(string sessionId)
    {
        if (!TryGetUserId(out int id)) return Unauthorized();

        UserSession? session = users.GetSession(sessionId);
        if (session is null || session.UserId != id)
            return NotFound();

        // Don't allow revoking the current session via this endpoint — use /auth/logout
        string? mySession = User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti);
        if (session.SessionId == mySession)
            return BadRequest(new { error = "Use /auth/logout to end your current session." });

        users.RevokeSession(sessionId);
        return Ok();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private bool TryGetUserId(out int id)
    {
        string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (int.TryParse(sub, out id)) return true;
        id = 0;
        return false;
    }
}

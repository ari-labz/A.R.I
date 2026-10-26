using System.Security.Claims;
using ARI.API.Auth;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>Everything about the person who is signed in: who they are, their display name, username, password and
/// the devices they are signed in on. Signing in and out is under /auth.</summary>
[Route("api/v1/me")]
[ApiController]
public class MeController(UserStore users) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        if (!TryGetUser(out User? user)) return Unauthorized();
        return Ok(new
        {
            user.Id,
            user.Username,
            user.Role,
            user.DisplayName,
            user.MustChangePassword,
            Sessions = users.GetSessionsForUser(user.Id).Select(s => new
            {
                s.SessionId,
                s.DeviceHint,
                s.IsDesktop,
                LastUsedAt = DateTimeOffset.FromUnixTimeSeconds(s.LastUsedAt).ToString("o"),
                ExpiresAt  = DateTimeOffset.FromUnixTimeSeconds(s.ExpiresAt).ToString("o"),
            }),
        });
    }

    /// <summary>Send only what changes. Changing the username or the password needs the current password.</summary>
    public record MePatch(string? DisplayName, string? Username, string? NewPassword, string? CurrentPassword);

    [HttpPatch]
    public IActionResult Patch([FromBody] MePatch req)
    {
        if (!TryGetUser(out User? user)) return Unauthorized();

        string? displayName = req.DisplayName?.Trim();
        if (displayName is { Length: 0 }) return BadRequest(new { error = "Display name cannot be empty." });

        string? username = req.Username?.Trim();
        bool changesLogin = username is not null || req.NewPassword is not null;
        if (changesLogin && !AuthService.VerifyPassword(req.CurrentPassword ?? "", user.PasswordHash))
            return BadRequest(new { error = "Current password is incorrect." });

        if (username is not null && AccountRules.CheckUsername(username) is { } usernameProblem)
            return BadRequest(new { error = usernameProblem });
        if (req.NewPassword is not null && AccountRules.CheckPassword(req.NewPassword) is { } passwordProblem)
            return BadRequest(new { error = passwordProblem });

        // Everything is checked before anything is changed, so a refused request changes nothing.
        if (username is not null && !username.Equals(user.Username, StringComparison.Ordinal) && !users.SetUsername(user.Id, username))
            return Conflict(new { error = "That username is already taken." });
        if (displayName is not null) users.SetDisplayName(user.Id, displayName);

        string? mySession = User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti);
        if (req.NewPassword is not null)
        {
            users.SetPassword(user.Id, AuthService.HashPassword(req.NewPassword), mustChange: false);
            // The new password is enforced everywhere: every other device is signed out.
            foreach (UserSession s in users.GetSessionsForUser(user.Id))
                if (s.SessionId != mySession) users.RevokeSession(s.SessionId);
        }

        // The sign-in token carries the old username, so after a rename every device signs in again to pick up the new one.
        if (username is not null)
        {
            users.RevokeAllSessionsForUser(user.Id);
            return Ok(new { signInAgain = true, message = "Username updated. Please sign in again." });
        }
        return Ok(new { signInAgain = false });
    }

    [HttpDelete("sessions/{id}")]
    public IActionResult RevokeSession(string id)
    {
        if (!TryGetUser(out User? user)) return Unauthorized();

        UserSession? session = users.GetSession(id);
        if (session is null || session.UserId != user.Id) return NotFound();

        // The session being used right now is ended with /auth/logout.
        string? mySession = User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti);
        if (session.SessionId == mySession)
            return BadRequest(new { error = "Use /api/v1/auth/logout to end your current session." });

        users.RevokeSession(id);
        return Ok();
    }

    private bool TryGetUser(out User user)
    {
        user = null!;
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int id)) return false;
        User? found = users.GetById(id);
        if (found is null) return false;
        user = found;
        return true;
    }
}

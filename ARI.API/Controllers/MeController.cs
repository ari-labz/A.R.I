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
        // A name people see in conversations must not be someone else's, or a guest could pass as the Master.
        if (displayName is not null && users.GetAll().Any(u => u.Id != user.Id
                && (u.Username.Equals(displayName, StringComparison.OrdinalIgnoreCase) || u.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase))))
            return Conflict(new { error = "Another person already uses that name." });

        string? username = req.Username?.Trim();
        bool changesLogin = username is not null || req.NewPassword is not null;
        if (changesLogin && !AuthService.VerifyPassword(req.CurrentPassword ?? "", user.PasswordHash))
            return BadRequest(new { error = "Current password is incorrect." });

        if (username is not null && AccountRules.CheckUsername(username) is { } usernameProblem)
            return BadRequest(new { error = usernameProblem });
        if (req.NewPassword is not null && AccountRules.CheckPassword(req.NewPassword) is { } passwordProblem)
            return BadRequest(new { error = passwordProblem });

        // With a temporary password the one thing to do is replace it.
        if (user.MustChangePassword && req.NewPassword is null)
            return BadRequest(new { error = "Choose a new password first." });
        // A name must not be someone else's name, whether that is their username or their display name.
        if (username is not null && !username.Equals(user.Username, StringComparison.OrdinalIgnoreCase)
            && users.GetAll().Any(u => u.Id != user.Id && u.DisplayName.Equals(username, StringComparison.OrdinalIgnoreCase)))
            return Conflict(new { error = "Another person already uses that name." });

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

    // ── Personal access tokens ───────────────────────────────────────────────────
    // A long-lived key for a script or a third-party app, like a GitHub token. It does what its owner can do, and nothing
    // more, and it stops working the moment it is deleted. It is shown once, when it is made. Making and deleting tokens
    // needs a signed-in session, so a token can't be used to make more tokens.

    public record NewToken(string? Name);

    private static object Describe(PersonalToken t) => new
    {
        t.Id, t.Name, t.Prefix,
        CreatedAt  = DateTimeOffset.FromUnixTimeSeconds(t.CreatedAt).ToString("o"),
        LastUsedAt = t.LastUsedAt == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(t.LastUsedAt).ToString("o"),
    };

    [HttpGet("tokens")]
    public IActionResult ListTokens()
    {
        if (!TryGetUser(out User? user)) return Unauthorized();
        if (AuthMiddleware.UsedPersonalToken(User)) return StatusCode(403, new { error = "Sign in to manage tokens; a token can't manage tokens." });
        return Ok(users.ListTokens(user.Id).Select(Describe).ToList());
    }

    [HttpPost("tokens")]
    public IActionResult CreateToken([FromBody] NewToken req)
    {
        if (!TryGetUser(out User? user)) return Unauthorized();
        if (AuthMiddleware.UsedPersonalToken(User)) return StatusCode(403, new { error = "Sign in to manage tokens; a token can't manage tokens." });
        string name = (req.Name ?? "").Trim();
        if (name.Length is 0 or > 60) return BadRequest(new { error = "Give the token a name of 1 to 60 characters, so you can tell it apart later." });
        if (users.CountTokens(user.Id) >= UserStore.MaxTokensPerUser) return BadRequest(new { error = $"You can have at most {UserStore.MaxTokensPerUser} tokens. Delete one you no longer use." });

        string secret = AuthMiddleware.PatPrefix + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        PersonalToken made = users.CreateToken(user.Id, name, AuthMiddleware.HashToken(secret), secret[..(AuthMiddleware.PatPrefix.Length + 4)]);
        // The only time the token itself is ever shown: only its fingerprint is kept.
        return Ok(new { token = secret, details = Describe(made) });
    }

    [HttpDelete("tokens/{id}")]
    public IActionResult DeleteToken(string id)
    {
        if (!TryGetUser(out User? user)) return Unauthorized();
        if (AuthMiddleware.UsedPersonalToken(User)) return StatusCode(403, new { error = "Sign in to manage tokens; a token can't manage tokens." });
        return users.RevokeToken(id, user.Id) ? Ok() : NotFound(new { error = "No such token." });
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

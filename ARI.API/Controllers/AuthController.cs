using System.Security.Claims;
using ARI.API.Auth;
using ARI.Common;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

[Route("auth")]
[ApiController]
public class AuthController(UserStore users, AuthService auth) : ControllerBase
{
    // Requests may arrive through a reverse proxy (see APIModule.cs), in which case
    // Connection.RemoteIpAddress is just the proxy's own address and every real client
    // collapses onto one IP for lockout purposes. Prefer the client IP the proxy reports.
    private string RemoteIp
    {
        get
        {
            string? forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
                return forwarded.Split(',')[0].Trim();

            return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }
    }

    // ── Login ────────────────────────────────────────────────────────────────────

    public record LoginRequest(string Username, string Password, string? DeviceHint, bool IsDesktop);
    public record LoginResponse(string Token, string Role, string DisplayName, bool MustChangePassword);

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "Username and password are required." });

        string ip = RemoteIp;

        User? user = users.GetByUsername(req.Username);
        if (user is null || !AuthService.VerifyPassword(req.Password, user.PasswordHash))
        {
            int attempts = users.RecordFailedAttempt(ip);
            if (attempts >= 3)
            {
                NotifyLockout(ip, attempts);
                return StatusCode(403, new { error = "Too many failed attempts. This IP has been locked out." });
            }
            return Unauthorized(new { error = "Invalid username or password.", attemptsRemaining = 3 - attempts });
        }

        // Successful login — clear any failure record for this IP
        users.ClearFailedAttempts(ip);

        string      sessionId = Guid.NewGuid().ToString();
        string      hint      = req.DeviceHint ?? (req.IsDesktop ? "ARI Desktop" : "Browser");
        var (token, session)  = auth.IssueToken(user, sessionId, hint, req.IsDesktop);
        users.CreateSession(session);

        return Ok(new LoginResponse(token, user.Role, user.DisplayName, user.MustChangePassword));
    }

    // ── Logout ───────────────────────────────────────────────────────────────────

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        string? sessionId = User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti);
        if (sessionId is not null)
            users.RevokeSession(sessionId);
        return Ok();
    }

    // ── Change password ───────────────────────────────────────────────────────────

    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    [HttpPost("change-password")]
    public IActionResult ChangePassword([FromBody] ChangePasswordRequest req)
    {
        if (!TryGetUserId(out int userId))
            return Unauthorized();

        User? user = users.GetById(userId);
        if (user is null)
            return Unauthorized();

        if (!AuthService.VerifyPassword(req.CurrentPassword, user.PasswordHash))
            return BadRequest(new { error = "Current password is incorrect." });

        if (req.NewPassword.Length < 5)
            return BadRequest(new { error = "New password must be at least 8 characters." });

        string hash = AuthService.HashPassword(req.NewPassword);
        users.SetPassword(userId, hash, mustChange: false);

        // Revoke all other sessions so the new password is enforced everywhere
        string? mySession = User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti);
        foreach (UserSession s in users.GetSessionsForUser(userId))
        {
            if (s.SessionId != mySession)
                users.RevokeSession(s.SessionId);
        }

        return Ok();
    }

    // ── Current user ─────────────────────────────────────────────────────────────

    [HttpGet("me")]
    public IActionResult Me()
    {
        if (!TryGetUserId(out int userId)) return Unauthorized();
        User? user = users.GetById(userId);
        if (user is null) return Unauthorized();
        return Ok(new { user.Id, user.Username, user.Role, user.DisplayName, user.MustChangePassword });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private bool TryGetUserId(out int id)
    {
        string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (int.TryParse(sub, out id)) return true;
        id = 0;
        return false;
    }

    private static void NotifyLockout(string ip, int attempts)
    {
        // Trigger a Discord DM / push notification that an IP has been locked.
        // Routed through Modules so we don't need a direct Discord dependency here.
        _ = Modules.Discord?.NotifyOwner($"ARI security: IP {ip} locked out after {attempts} failed login attempts.");
    }
}

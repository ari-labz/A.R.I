using System.Security.Claims;
using ARI.API.Auth;
using ARI.Common;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

[Route("api/v1/auth")]
[ApiController]
public class AuthController(UserStore users, AuthService auth) : ControllerBase
{
    // ── Login ────────────────────────────────────────────────────────────────────

    public record LoginRequest(string Username, string Password, string? DeviceHint, bool IsDesktop);
    public record LoginResponse(string Token, string Role, string DisplayName, bool MustChangePassword);

    private static readonly string DummyHash = AuthService.HashPassword("not-a-real-password");

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "Username and password are required." });

        string ip = ClientAddress.Of(HttpContext);

        User? user = users.GetByUsername(req.Username);
        // Always do the same amount of work, so the time taken doesn't reveal whether the username exists.
        bool passwordOk = AuthService.VerifyPassword(req.Password, user?.PasswordHash ?? DummyHash);
        if (user is null || !passwordOk)
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
        (string token, UserSession session)  = auth.IssueToken(user, sessionId, hint, req.IsDesktop);
        users.CreateSession(session);
        AuthCookie.Set(Response, token, req.IsDesktop, ClientAddress.IsHttps(HttpContext));

        return Ok(new LoginResponse(token, user.Role, user.DisplayName, user.MustChangePassword));
    }

    // ── Logout ───────────────────────────────────────────────────────────────────

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        string? sessionId = User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti);
        if (sessionId is not null)
            users.RevokeSession(sessionId);
        AuthCookie.Clear(Response);
        return Ok();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static void NotifyLockout(string ip, int attempts)
    {
        _ = Modules.Llm?.NotifyOwner($"ARI security: IP {ip} locked out after {attempts} failed login attempts.");
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using ARI.Common;
using Microsoft.IdentityModel.Tokens;

namespace ARI.API.Auth;

/// <summary>
/// Issues and validates JWTs, hashes passwords, and manages the signing key.
/// The signing key is generated once and persisted to Paths.Keys/jwt-key.bin.
/// </summary>
public class AuthService
{
    private const int BrowserTokenDays = 30;
    private const int DesktopTokenDays = 30;

    /// <summary>How long a token issued for this client kind stays valid.</summary>
    public static TimeSpan TokenLifetime(bool isDesktop) =>
        TimeSpan.FromDays(isDesktop ? DesktopTokenDays : BrowserTokenDays);

    private readonly SymmetricSecurityKey signingKey;
    private readonly JwtSecurityTokenHandler handler = new();

    public AuthService()
    {
        signingKey = new SymmetricSecurityKey(LoadOrCreateKey());
    }

    // ── JWT ─────────────────────────────────────────────────────────────────────

    public (string token, UserSession session) IssueToken(User user, string sessionId, string deviceHint, bool isDesktop)
    {
        long   expUnix = DateTimeOffset.UtcNow.Add(TokenLifetime(isDesktop)).ToUnixTimeSeconds();
        long   nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        ClaimsIdentity identity = new(
        [
            new Claim(JwtRegisteredClaimNames.Sub,  user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti,  sessionId),
            new Claim(ClaimTypes.Name,               user.Username),
            new Claim(ClaimTypes.Role,               user.Role),
            new Claim("displayName",                 user.DisplayName),
            new Claim("mustChangePassword",          user.MustChangePassword ? "true" : "false"),
        ]);

        SecurityTokenDescriptor descriptor = new()
        {
            Subject            = identity,
            Expires            = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime,
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
        };

        SecurityToken  token    = handler.CreateToken(descriptor);
        string         tokenStr = handler.WriteToken(token);

        UserSession session = new()
        {
            SessionId  = sessionId,
            UserId     = user.Id,
            ExpiresAt  = expUnix,
            DeviceHint = deviceHint,
            IsDesktop  = isDesktop,
            LastUsedAt = nowUnix,
        };

        return (tokenStr, session);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            return handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer           = false,
                ValidateAudience         = false,
                ValidateLifetime         = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey         = signingKey,
                ClockSkew                = TimeSpan.FromMinutes(1),
            }, out _);
        }
        catch
        {
            return null;
        }
    }

    // ── Passwords ────────────────────────────────────────────────────────────────

    public static string HashPassword(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);

    public static bool VerifyPassword(string password, string hash) =>
        BCrypt.Net.BCrypt.Verify(password, hash);

    public static string GenerateRandomPassword(int length = 8)
    {
        const string chars = "ABCDEFGHJKMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(chars, length);
    }

    // ── Signing key ──────────────────────────────────────────────────────────────

    private static byte[] LoadOrCreateKey()
    {
        string path = Path.Combine(Paths.Keys, "jwt-key.bin");
        if (File.Exists(path))
            return File.ReadAllBytes(path);

        byte[] key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Paths.Keys);
        File.WriteAllBytes(path, key);
        return key;
    }
}

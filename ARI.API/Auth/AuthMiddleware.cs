using System.Security.Claims;
using ARI.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ARI.API.Auth;

/// <summary>
/// Runs before every request:
///   1. IP blocklist — blocked IPs get a silent 403 before any logic runs.
///   2. JWT validation — extracts and validates the bearer token.
///   3. Session check — confirms the session ID (jti) still exists in the DB (supports revocation).
/// Endpoints decorated with [AllowAnonymous] bypass step 2+3 (login endpoint).
/// Step 1 always runs.
/// </summary>
public class AuthMiddleware(RequestDelegate next, ILogger<AuthMiddleware> log)
{
    private static readonly HashSet<string> AnonPaths =
    [
        "/auth/login",
    ];

    private static readonly string[] AdminPrefixes =
    [
        "/admin/",
        "/controlpanel.html",
    ];

    public async Task InvokeAsync(HttpContext ctx)
    {
        UserStore store   = ctx.RequestServices.GetRequiredService<UserStore>();
        AuthService auth  = ctx.RequestServices.GetRequiredService<AuthService>();

        string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Step 1: IP blocklist (always)
        if (store.IsBlocked(ip))
        {
            log.LogWarning("Blocked request from {Ip}", ip);
            ctx.Response.StatusCode = 403;
            return;
        }

        // Step 2+3: JWT + session (skip for anon paths)
        if (!AnonPaths.Contains(ctx.Request.Path.Value ?? ""))
        {
            string? token = ExtractBearer(ctx);
            if (token is null)
            {
                ctx.Response.StatusCode = 401;
                return;
            }

            ClaimsPrincipal? principal = auth.ValidateToken(token);
            if (principal is null)
            {
                ctx.Response.StatusCode = 401;
                return;
            }

            string? sessionId = principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti);
            if (sessionId is null || store.GetSession(sessionId) is null)
            {
                ctx.Response.StatusCode = 401;
                return;
            }

            ctx.User = principal;
            store.TouchSession(sessionId);

            string? subStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (int.TryParse(subStr, out int userId))
                store.TouchLastActive(userId);

            // Admin-only paths — guests get a flat 403
            string path = ctx.Request.Path.Value ?? "";
            if (Array.Exists(AdminPrefixes, p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                if (principal.FindFirstValue(ClaimTypes.Role) != Roles.Admin)
                {
                    ctx.Response.StatusCode = 403;
                    return;
                }
            }
        }

        await next(ctx);
    }

    private static string? ExtractBearer(HttpContext ctx)
    {
        string? header = ctx.Request.Headers.Authorization;
        if (header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();
        // EventSource and WebSocket connections cannot set custom headers — accept token as query param.
        string? queryToken = ctx.Request.Query["token"];
        if (!string.IsNullOrEmpty(queryToken))
            return queryToken;
        return null;
    }
}

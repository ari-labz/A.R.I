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
        "/api/v1/auth/login",
        "/api/v1/theme",
        "/api/v1/branding",
        "/api/v1/setup/status",
        "/api/v1/setup/account",
    ];

    /// <summary>The admin half of the API. Everything under it is for admins only, so a new admin route is protected
    /// by where it lives; nothing has to be remembered or added to a list. Everything else is open to any signed-in
    /// person, and each route that holds someone's own data checks ownership itself.</summary>
    internal const string AdminPrefix = "/api/v1/admin/";

    /// <summary>True if a guest must be refused.</summary>
    internal static bool IsAdminOnly(string path)
        => path.StartsWith(AdminPrefix, StringComparison.OrdinalIgnoreCase)
        || path.Equals("/controlpanel.html", StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext ctx)
    {
        UserStore store   = ctx.RequestServices.GetRequiredService<UserStore>();
        AuthService auth  = ctx.RequestServices.GetRequiredService<AuthService>();

        string ip = ClientAddress.Of(ctx);

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
            if (IsAdminOnly(path))
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
        // EventSource, WebSocket, <img> and download links cannot set custom headers, so a read (GET) may carry the
        // token in the address. Anything that changes data may not: addresses end up in logs and browser history.
        if (HttpMethods.IsGet(ctx.Request.Method))
        {
            string? queryToken = ctx.Request.Query["token"];
            if (!string.IsNullOrEmpty(queryToken))
                return queryToken;
        }
        // Browsers keep the session in a cookie, so a fresh tab (or a direct hit on
        // /controlpanel.html) is still signed in without passing the token around.
        string? cookieToken = ctx.Request.Cookies[AuthCookie.Name];
        if (!string.IsNullOrEmpty(cookieToken))
            return cookieToken;
        return null;
    }

    /// <summary>
    /// Same JWT+session check as InvokeAsync, exposed for callers that run before this middleware in the
    /// pipeline (the raw WebSocket handlers in APIModule, which must authenticate before accepting the
    /// upgrade). Returns the validated principal, or null if the request is not authenticated.
    /// </summary>
    public static ClaimsPrincipal? Authenticate(HttpContext ctx, UserStore store, AuthService auth)
    {
        string ip = ClientAddress.Of(ctx);
        if (store.IsBlocked(ip))
            return null;

        string? token = ExtractBearer(ctx);
        if (token is null)
            return null;

        ClaimsPrincipal? principal = auth.ValidateToken(token);
        if (principal is null)
            return null;

        string? sessionId = principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti);
        if (sessionId is null || store.GetSession(sessionId) is null)
            return null;

        store.TouchSession(sessionId);

        string? subStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (int.TryParse(subStr, out int userId))
            store.TouchLastActive(userId);

        return principal;
    }
}

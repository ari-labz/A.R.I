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

    private static readonly string[] AdminPrefixes =
    [
        "/api/v1/admin/",
        "/controlpanel.html",
    ];

    // Control-panel controllers whose routes don't live under /admin/. Every path under these is admin-only,
    // except the few things the chat UI itself needs and so are listed as exceptions in IsAdminOnly.
    private static readonly string[] AdminOnlyAreas =
    [
        "/api/v1/agents", "/api/v1/pipeline-graphs", "/api/v1/servers", "/api/v1/modules", "/api/v1/imagegen", "/api/v1/persona/proposals", "/api/v1/models", "/api/v1/voice", "/api/v1/feedback",
    ];

    /// <summary>True if a guest must be refused. A path is matched on whole segments ("/api/v1/models" but not "/api/v1/modelsX").</summary>
    internal static bool IsAdminOnly(string path, string method)
    {
        if (Array.Exists(AdminPrefixes, p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;

        bool inArea = Array.Exists(AdminOnlyAreas, a =>
            path.Equals(a, StringComparison.OrdinalIgnoreCase) || path.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase));
        if (!inArea) return false;

        bool post = method.Equals("POST", StringComparison.OrdinalIgnoreCase);
        bool get  = method.Equals("GET",  StringComparison.OrdinalIgnoreCase);

        // What an ordinary signed-in user's chat page uses: reading the reasoning dial, speaking a reply aloud,
        // and thumbs up/down on a response in a thread they can open (the controller checks the thread).
        if (get  && path.Equals("/api/v1/models/reasoning-effort", StringComparison.OrdinalIgnoreCase)) return false;
        if (post && (path.Equals("/api/v1/voice/speak", StringComparison.OrdinalIgnoreCase)
                  || path.Equals("/api/v1/voice/split-sentences", StringComparison.OrdinalIgnoreCase))) return false;
        if (post && path.Equals("/api/v1/feedback", StringComparison.OrdinalIgnoreCase)) return false;
        if (get  && path.StartsWith("/api/v1/feedback/thread/", StringComparison.OrdinalIgnoreCase)) return false;
        // Changing or withdrawing a rating (the controller checks the rating's thread is theirs).
        if ((method.Equals("PATCH", StringComparison.OrdinalIgnoreCase) || method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            && path.StartsWith("/api/v1/feedback/", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

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
            if (IsAdminOnly(path, ctx.Request.Method))
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

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
/// A short list of addresses (AnonPaths) bypass step 2+3: login, the open setup steps, the theme and health.
/// Step 1 always runs.
/// </summary>
public class AuthMiddleware(RequestDelegate next, ILogger<AuthMiddleware> log)
{
    private static readonly HashSet<string> AnonPaths =
    [
        "/api/v1/auth/login",
        "/api/v1/health",
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
        => path.StartsWith(AdminPrefix, StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext ctx)
    {
        UserStore store   = ctx.RequestServices.GetRequiredService<UserStore>();
        AuthService auth  = ctx.RequestServices.GetRequiredService<AuthService>();

        string ip = ClientAddress.Of(ctx);

        // A page on another site (or another port on this machine) must not be able to make a signed-in browser change
        // things: when the browser says where a changing request came from, it has to be this server.
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method)
            && ctx.Request.Headers.Origin.ToString() is { Length: > 0 } origin
            && (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri) || !originUri.Authority.Equals(ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            await Deny(ctx, 403, "Cross-site requests are not allowed.");
            return;
        }

        // Step 1: IP blocklist (always)
        if (store.IsBlocked(ip))
        {
            log.LogWarning("Blocked request from {Ip}", ip);
            await Deny(ctx, 403, "Too many failed sign-in attempts from this address. Try again later, or ask an admin to unblock it.");
            return;
        }

        // Step 2+3: JWT + session (skip for anon paths)
        if (!AnonPaths.Contains(ctx.Request.Path.Value ?? ""))
        {
            string? token = ExtractBearer(ctx);
            if (token is null)
            {
                await Unauthenticated(ctx);
                return;
            }

            ClaimsPrincipal? principal = ResolvePrincipal(token, store, auth);
            if (principal is null)
            {
                await Unauthenticated(ctx);
                return;
            }

            string? sessionId = principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti);
            if (sessionId is null || (!sessionId.StartsWith(PatSessionPrefix, StringComparison.Ordinal) && store.GetSession(sessionId) is null))
            {
                await Unauthenticated(ctx);
                return;
            }

            ctx.User = principal;
            store.TouchSession(sessionId);

            string? subStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (int.TryParse(subStr, out int userId))
                store.TouchLastActive(userId);

            string path = ctx.Request.Path.Value ?? "";

            // A temporary password must be replaced before anything else works: only reading who you are, changing it
            // (PATCH /me) and signing out are allowed until then.
            if (int.TryParse(subStr, out int mustId) && store.GetById(mustId)?.MustChangePassword == true
                && !(path.Equals("/api/v1/me", StringComparison.OrdinalIgnoreCase) && (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsPatch(ctx.Request.Method)))
                && !path.Equals("/api/v1/auth/logout", StringComparison.OrdinalIgnoreCase))
            {
                await Deny(ctx, 403, "Change your temporary password first.");
                return;
            }

            // Admin-only paths — guests get a flat 403
            if (IsAdminOnly(path))
            {
                if (principal.FindFirstValue(ClaimTypes.Role) != Roles.Admin)
                {
                    await Deny(ctx, 403, "Only an admin can do this.");
                    return;
                }
            }
        }

        await next(ctx);
    }

    public const string PatPrefix = "ari_pat_";
    public const string PatSessionPrefix = "pat:";

    /// <summary>The hash a personal token is stored and looked up by.</summary>
    public static string HashToken(string token) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>Turns what the caller presented into who they are: a login token (checked as before), or a personal access
    /// token, which carries the permissions of the person who made it as they are right now (their role is read fresh, so a
    /// demotion or deletion takes effect at once).</summary>
    private static ClaimsPrincipal? ResolvePrincipal(string token, UserStore store, AuthService auth)
    {
        if (!token.StartsWith(PatPrefix, StringComparison.Ordinal)) return auth.ValidateToken(token);

        PersonalToken? pat = store.FindTokenByHash(HashToken(token));
        if (pat is null || store.GetById(pat.UserId) is not { } owner) return null;
        store.TouchToken(pat.Id, pat.LastUsedAt);
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, owner.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier,                                   owner.Id.ToString()),
            new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti, PatSessionPrefix + pat.Id),
            new Claim(ClaimTypes.Name,                                             owner.Username),
            new Claim(ClaimTypes.Role,                                             owner.Role),
            new Claim("displayName",                                               owner.DisplayName),
            new Claim("mustChangePassword",                                        owner.MustChangePassword ? "true" : "false"),
        ], "PersonalToken"));
    }

    /// <summary>True when this request was made with a personal access token rather than a signed-in session.</summary>
    public static bool UsedPersonalToken(ClaimsPrincipal user)
        => user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.StartsWith(PatSessionPrefix, StringComparison.Ordinal) == true;

    /// <summary>Not signed in. Signing out when you already are signed out is not an error: it just clears the cookie.</summary>
    private static async Task Unauthenticated(HttpContext ctx)
    {
        if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.Equals("/api/v1/auth/logout", StringComparison.OrdinalIgnoreCase))
        {
            AuthCookie.Clear(ctx.Response);
            await ctx.Response.WriteAsJsonAsync(new { ok = true });
            return;
        }
        await Deny(ctx, 401, "Sign in first.");
    }

    /// <summary>Refuses a request with the same JSON error shape every other failure uses.</summary>
    private static async Task Deny(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { error = message });
    }

    private static string? ExtractBearer(HttpContext ctx)
    {
        string? header = ctx.Request.Headers.Authorization;
        if (header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();
        // EventSource, WebSocket, <img> and download links cannot set custom headers, so they rely on the session
        // cookie instead. The token is never accepted in the address: addresses end up in logs and browser history.
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

        ClaimsPrincipal? principal = ResolvePrincipal(token, store, auth);
        if (principal is null)
            return null;

        string? sessionId = principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti);
        if (sessionId is null || (!sessionId.StartsWith(PatSessionPrefix, StringComparison.Ordinal) && store.GetSession(sessionId) is null))
            return null;

        // Same rule as the REST routes: a temporary password has to be replaced before anything else.
        if (int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out int mustId) && store.GetById(mustId)?.MustChangePassword == true)
            return null;

        store.TouchSession(sessionId);

        string? subStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (int.TryParse(subStr, out int userId))
            store.TouchLastActive(userId);

        return principal;
    }
}

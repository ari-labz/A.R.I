using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ARI.API.Auth;

/// <summary>
/// Who is calling, and over what. The address decides sign-in lockouts, so it must not be something a caller can
/// make up: the <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> headers are only believed when the server is
/// told it sits behind a proxy of its own (<c>TrustForwardedHeaders</c> in AriConfig.json). Without that setting
/// they are ignored and the address is the other end of the connection.
/// </summary>
public static class ClientAddress
{
    public static string Of(HttpContext ctx)
    {
        if (Trusts(ctx))
        {
            // The proxy adds the address it saw to the end of the list, so the last entry is the one it vouches for.
            string? forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrWhiteSpace(forwarded))
                return forwarded.Split(',')[^1].Trim();
        }
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public static bool IsHttps(HttpContext ctx)
        => ctx.Request.IsHttps
           || (Trusts(ctx) && ctx.Request.Headers["X-Forwarded-Proto"].ToString().Equals("https", StringComparison.OrdinalIgnoreCase));

    private static bool Trusts(HttpContext ctx)
        => ctx.RequestServices.GetService<APIConfig>()?.TrustForwardedHeaders == true;
}

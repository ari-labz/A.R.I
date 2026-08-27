using Microsoft.AspNetCore.Http;

namespace ARI.API.Auth;

/// <summary>
/// The browser session cookie. The token also travels in the Authorization header while a tab
/// is open; the cookie is what survives closing the tab, so the user is not asked to log in again
/// every time they come back. HttpOnly — page scripts never read it, only the server does.
/// </summary>
public static class AuthCookie
{
    public const string Name = "ari_session";

    public static void Set(HttpResponse response, string token, bool isDesktop, bool isHttps)
    {
        response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            Secure   = isHttps,
            SameSite = SameSiteMode.Lax,
            Path     = "/",
            Expires  = DateTimeOffset.UtcNow.Add(AuthService.TokenLifetime(isDesktop)),
        });
    }

    public static void Clear(HttpResponse response) =>
        response.Cookies.Delete(Name, new CookieOptions { Path = "/" });
}

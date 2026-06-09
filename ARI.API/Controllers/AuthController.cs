using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

[Route("auth")]
public class AuthController : Controller
{
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = "/")
    {
        return Challenge(
            new AuthenticationProperties { RedirectUri = returnUrl ?? "/" },
            GoogleDefaults.AuthenticationScheme);
    }

    [HttpGet("callback")]
    public IActionResult Callback() => Redirect("/");

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/auth/login");
    }
}

using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

[ApiController]
public class ThemeController : ControllerBase
{
    // Read is public (AuthMiddleware.AnonPaths) so the login screen is themed too — colours aren't secret.
    [HttpGet("api/v1/theme")]
    public IActionResult Get() => Ok(ThemeStore.Get());

    [HttpPost("api/v1/admin/theme")]
    public IActionResult Set([FromBody] Theme theme)
    {
        string? err = ThemeStore.Set(theme);
        return err is null ? Ok(ThemeStore.Get()) : BadRequest(new { error = err });
    }

    [HttpDelete("api/v1/admin/theme")]
    public IActionResult Reset()
    {
        ThemeStore.Reset();
        return Ok(ThemeStore.Get());
    }
}

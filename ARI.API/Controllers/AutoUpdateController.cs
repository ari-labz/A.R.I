using ARI.Common;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>The "Update automatically" switch in the control panel, and why it can or can't act on this run.</summary>
[Route("api/v1/admin/autoUpdate")]
[ApiController]
public class AutoUpdateController : ControllerBase
{
    public sealed record Body(bool Enabled);

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        await UpdateCheck.EnsureCheckedAsync();
        AutoUpdateStore.State s = AutoUpdateStore.Get();
        string? blocked = Shared.DevMode ? "This is a dev run, so it never updates itself."
            : Environment.GetEnvironmentVariable("APP_INSTALL_ROOT") is null ? "This server runs from source rather than an install, so there's nothing for it to update."
            : null;
        return Ok(new
        {
            enabled  = s.Enabled,
            current  = UpdateCheck.ServerVersion,
            latest   = UpdateCheck.LatestVersion,
            outdated = UpdateCheck.Outdated,
            blocked,
            lastResult = s.LastResult,
            lastAt     = s.LastAt,
        });
    }

    [HttpPost]
    public IActionResult Set([FromBody] Body body)
    {
        AutoUpdateStore.Update(s => s.Enabled = body.Enabled);
        return Ok(new { enabled = body.Enabled });
    }
}

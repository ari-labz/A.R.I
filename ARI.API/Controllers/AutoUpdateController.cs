using ARI.Common;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>The "Update automatically" switch and "Check now" button in the control panel, and why they can or can't act on this run.</summary>
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
        string? blocked = Blocked();
        return Ok(new
        {
            enabled  = s.Enabled,
            current  = UpdateCheck.ServerVersion,
            latest   = UpdateCheck.LatestVersion,
            outdated = UpdateCheck.Outdated,
            blocked,
            lastResult = s.LastResult,
            lastAt     = s.LastAt,
            busy       = AutoUpdateStore.Busy,
        });
    }

    private static string? Blocked() =>
        Shared.DevMode ? "This is a dev run, so it never updates itself."
        : Environment.GetEnvironmentVariable("APP_INSTALL_ROOT") is null ? "This server runs from source rather than an install, so there's nothing for it to update."
        : null;

    /// <summary>"Check now": asks GitHub straight away. With "Update automatically" on, also wakes the updater so a newer
    /// version is downloaded and switched to (after the usual quiet wait) without waiting for the hourly check.</summary>
    [HttpPost("check")]
    public async Task<IActionResult> CheckNow()
    {
        await UpdateCheck.EnsureCheckedAsync(force: true);
        AutoUpdateStore.State s = AutoUpdateStore.Get();
        bool woke = s.Enabled && Blocked() is null && !AutoUpdateStore.Busy && AutoUpdateStore.RequestCheck();
        string message = !UpdateCheck.Outdated ? $"{UpdateCheck.ServerVersion} is the newest version."
            : woke ? $"{UpdateCheck.LatestVersion} is out. Downloading it now; she switches over after ten quiet minutes."
            : AutoUpdateStore.Busy ? $"{UpdateCheck.LatestVersion} is out and already being handled."
            : $"{UpdateCheck.LatestVersion} is out.{(s.Enabled ? "" : " Turn on Update automatically to install it, or use the Installer.")}";
        return Ok(new { latest = UpdateCheck.LatestVersion, outdated = UpdateCheck.Outdated, started = woke, message });
    }

    [HttpPost]
    public IActionResult Set([FromBody] Body body)
    {
        AutoUpdateStore.Update(s => s.Enabled = body.Enabled);
        return Ok(new { enabled = body.Enabled });
    }
}

using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

[Route("api/info")]
[ApiController]
public class InfoController : ControllerBase
{
    // Bump this whenever a breaking change requires clients to update.
    private const string RequiredClientVersion = "0.6.0";

    [HttpGet("version")]
    public async Task<IActionResult> GetVersion()
    {
        await UpdateCheck.EnsureCheckedAsync();
        return Ok(new
        {
            requiredClientVersion = RequiredClientVersion,
            serverVersion         = UpdateCheck.ServerVersion,
            latestVersion         = UpdateCheck.LatestVersion,
            serverOutdated        = UpdateCheck.Outdated,
        });
    }
}

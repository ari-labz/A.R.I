using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

[Route("api/info")]
[ApiController]
public class InfoController : ControllerBase
{
    // Bump this whenever a breaking change requires clients to update.
    private const string RequiredClientVersion = "0.2.3";

    [HttpGet("version")]
    public IActionResult GetVersion() =>
        Ok(new { requiredClientVersion = RequiredClientVersion });
}

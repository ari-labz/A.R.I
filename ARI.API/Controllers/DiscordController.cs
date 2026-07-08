using ARI.Common;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>Control-panel endpoints for the Discord integration.</summary>
[Route("admin/discord")]
[ApiController]
public class DiscordController : ControllerBase
{
    /// <summary>Deletes every message ARI sent in the last 24 hours.</summary>
    [HttpPost("delete-recent")]
    public async Task<IActionResult> DeleteRecent()
    {
        if (Modules.Discord is null) return BadRequest(new { message = "Discord is not available." });
        int count = await Modules.Discord.DeleteRecentMessagesAsync(TimeSpan.FromHours(24));
        return Ok(new { message = $"Deleted {count} message{(count == 1 ? "" : "s")}." });
    }
}

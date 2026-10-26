using ARI.Common;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>Control-panel endpoints for the Discord integration.</summary>
[Route("api/v1/admin/integrations/discord")]
[ApiController]
public class DiscordController : ControllerBase
{
    /// <summary>Deletes every message ARI sent in the last 24 hours.</summary>
    [HttpPost("deleteRecent")]
    public async Task<IActionResult> DeleteRecent()
    {
        if (Modules.Discord is null) return BadRequest(new { error = "Discord is not available." });
        int count = await Modules.Discord.DeleteRecentMessagesAsync(TimeSpan.FromHours(24));
        return Ok(new { message = $"Deleted {count} message{(count == 1 ? "" : "s")}." });
    }

    /// <summary>Current settings. The token is never returned — only whether one is set.</summary>
    [HttpGet]
    public IActionResult GetConfig()
    {
        DiscordSettings s = DiscordStore.Get();
        return Ok(new
        {
            enabled            = s.Enabled,
            tokenConfigured    = !string.IsNullOrWhiteSpace(s.Token),
            masterDiscordId    = s.MasterDiscordId.ToString(),
            whitelistedUserIds = s.WhitelistedUserIds.Select(x => x.ToString()),
            watchedChannelIds  = s.WatchedChannelIds.Select(x => x.ToString()),
            allowedGuildIds    = s.AllowedGuildIds.Select(x => x.ToString()),
        });
    }

    /// <summary>Changes settings; send only what changes (a list that is sent replaces the whole list). A blank
    /// token leaves the stored one alone, so the panel never has to hold it to save anything else. Takes effect on restart.</summary>
    [HttpPatch]
    public IActionResult SetConfig([FromBody] DiscordConfigRequest req)
    {
        DiscordSettings current = DiscordStore.Get();
        // Who the Master is on Discord decides who controls ARI there: only the Master may change it.
        if (req.MasterDiscordId is not null && req.MasterDiscordId.Trim() != current.MasterDiscordId.ToString()
            && !(current.MasterDiscordId == 0 && string.IsNullOrWhiteSpace(req.MasterDiscordId))
            && User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value != ARI.Common.Shared.MasterUserId)
            return StatusCode(403, new { error = "Only the Master can change the Master's Discord ID." });

        List<ulong> users = current.WhitelistedUserIds, channels = current.WatchedChannelIds, guilds = current.AllowedGuildIds;
        if (req.WhitelistedUserIds is not null && !TryIds(req.WhitelistedUserIds, out users)) return BadRequest(new { error = "Whitelisted user IDs must be numeric." });
        if (req.WatchedChannelIds  is not null && !TryIds(req.WatchedChannelIds,  out channels)) return BadRequest(new { error = "Watched channel IDs must be numeric." });
        if (req.AllowedGuildIds    is not null && !TryIds(req.AllowedGuildIds,    out guilds)) return BadRequest(new { error = "Guild IDs must be numeric." });

        ulong masterDiscordId = current.MasterDiscordId;
        if (req.MasterDiscordId is not null)
        {
            masterDiscordId = 0;
            if (!string.IsNullOrWhiteSpace(req.MasterDiscordId) && !ulong.TryParse(req.MasterDiscordId.Trim(), out masterDiscordId))
                return BadRequest(new { error = "Master's Discord ID must be numeric." });
        }

        DiscordStore.Set(new DiscordSettings
        {
            Enabled            = req.Enabled ?? current.Enabled,
            Token              = string.IsNullOrWhiteSpace(req.Token) ? current.Token : req.Token.Trim(),
            MasterDiscordId    = masterDiscordId,
            WhitelistedUserIds = users,
            WatchedChannelIds  = channels,
            AllowedGuildIds    = guilds,
        });

        return Ok(new { ok = true, restartRequired = true });
    }

    private static bool TryIds(List<string>? raw, out List<ulong> parsed)
    {
        parsed = [];
        foreach (string s in raw ?? [])
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (!ulong.TryParse(s.Trim(), out ulong id)) return false;
            if (!parsed.Contains(id)) parsed.Add(id);
        }
        return true;
    }
}

// IDs travel as strings: they are 64-bit snowflakes and JavaScript numbers lose precision above 2^53.
public sealed class DiscordConfigRequest
{
    public bool? Enabled { get; set; }
    public string? Token { get; set; }
    public string? MasterDiscordId { get; set; }
    public List<string>? WhitelistedUserIds { get; set; }
    public List<string>? WatchedChannelIds { get; set; }
    public List<string>? AllowedGuildIds { get; set; }
}

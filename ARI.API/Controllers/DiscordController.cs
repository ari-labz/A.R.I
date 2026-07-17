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

    /// <summary>Current settings. The token is never returned — only whether one is set.</summary>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        DiscordSettings s = DiscordStore.Get();
        return Ok(new
        {
            enabled            = s.Enabled,
            tokenConfigured    = !string.IsNullOrWhiteSpace(s.Token),
            ownerId            = s.OwnerId.ToString(),
            whitelistedUserIds = s.WhitelistedUserIds.Select(x => x.ToString()),
            watchedChannelIds  = s.WatchedChannelIds.Select(x => x.ToString()),
            allowedGuildIds    = s.AllowedGuildIds.Select(x => x.ToString()),
        });
    }

    /// <summary>Saves settings. A blank token leaves the stored one alone, so the panel never has to
    /// hold it to save anything else. Takes effect on restart.</summary>
    [HttpPut("config")]
    public IActionResult SetConfig([FromBody] DiscordConfigRequest req)
    {
        DiscordSettings current = DiscordStore.Get();

        if (!TryIds(req.WhitelistedUserIds, out List<ulong> users))    return BadRequest(new { error = "Whitelisted user IDs must be numeric." });
        if (!TryIds(req.WatchedChannelIds,  out List<ulong> channels)) return BadRequest(new { error = "Watched channel IDs must be numeric." });
        if (!TryIds(req.AllowedGuildIds,    out List<ulong> guilds))   return BadRequest(new { error = "Guild IDs must be numeric." });

        ulong ownerId = 0;
        if (!string.IsNullOrWhiteSpace(req.OwnerId) && !ulong.TryParse(req.OwnerId.Trim(), out ownerId))
            return BadRequest(new { error = "Owner ID must be numeric." });

        DiscordStore.Set(new DiscordSettings
        {
            Enabled            = req.Enabled,
            Token              = string.IsNullOrWhiteSpace(req.Token) ? current.Token : req.Token.Trim(),
            OwnerId            = ownerId,
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
    public bool Enabled { get; set; }
    public string? Token { get; set; }
    public string? OwnerId { get; set; }
    public List<string>? WhitelistedUserIds { get; set; }
    public List<string>? WatchedChannelIds { get; set; }
    public List<string>? AllowedGuildIds { get; set; }
}

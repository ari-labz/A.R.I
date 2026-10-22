using System.Text.Json;
using System.Text.Json.Nodes;
using ARI.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ARI.API.Controllers;

/// <summary>General settings shown on the control panel's Settings page. Under /admin/, so admin only.</summary>
[Route("admin/settings")]
[ApiController]
public class SettingsController(ILogger<SettingsController> logger) : ControllerBase
{
    public record LoggingRequest(bool Enabled, bool IncludeFullRequests, bool IncludeToolResults, int RetentionDays);

    /// <summary>The session recorder (DTI, dream and chat logs): what it keeps and for how long. Read when ARI starts.</summary>
    [HttpGet("logging")]
    public IActionResult GetLogging()
    {
        JsonNode? rec = ReadRoot()?["Recording"];
        return Ok(new
        {
            enabled             = rec?["Enabled"]?.GetValue<bool>()             ?? true,
            includeFullRequests = rec?["IncludeFullRequests"]?.GetValue<bool>() ?? true,
            includeToolResults  = rec?["IncludeToolResults"]?.GetValue<bool>()  ?? true,
            retentionDays       = rec?["RetentionDays"]?.GetValue<int>()        ?? 30,
        });
    }

    [HttpPut("logging")]
    public IActionResult SetLogging([FromBody] LoggingRequest req)
    {
        if (req.RetentionDays < 0) return BadRequest(new { error = "Retention days can't be negative." });
        try
        {
            JsonObject root = ReadRoot() ?? new JsonObject();
            if (root["Recording"] is not JsonObject rec) root["Recording"] = rec = new JsonObject();
            rec["Enabled"]             = req.Enabled;
            rec["IncludeFullRequests"] = req.IncludeFullRequests;
            rec["IncludeToolResults"]  = req.IncludeToolResults;
            rec["RetentionDays"]       = req.RetentionDays;
            System.IO.File.WriteAllText(Paths.AriConfig, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return Ok(new { restartRequired = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Settings] Failed to write {Path}.", Paths.AriConfig);
            return StatusCode(500, new { error = $"Could not save AriConfig.json: {ex.Message}" });
        }
    }

    private static JsonObject? ReadRoot()
    {
        if (!System.IO.File.Exists(Paths.AriConfig)) return null;
        return JsonNode.Parse(System.IO.File.ReadAllText(Paths.AriConfig),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
    }
}

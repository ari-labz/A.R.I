using System.Text.Json;
using System.Text.Json.Nodes;
using ARI.API.Data;
using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ARI.API.Controllers;

/// <summary>
/// Every general setting in one place: read them all with GET, change any of them with PATCH (send only what changes).
/// Under /admin/, so admin only.
/// </summary>
[Route("api/v1/admin/settings")]
[ApiController]
public class SettingsController(PersistentData persistentData, ILogger<SettingsController> logger) : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    public record LoggingSettings(bool Enabled, bool IncludeFullRequests, bool IncludeToolResults, int RetentionDays);

    /// <summary>Any field left out (null) is left as it was.</summary>
    public record SettingsPatch(
        string? UserName,
        string? Conventions,
        string? SafeModePrompt,
        string? Persona,
        bool? DreamingEnabled,
        int? EngramTurns,
        int? ReasoningEffort,
        bool? PersistedThreadsEnabled,
        bool? EngramOthers,
        LoggingSettings? Logging);

    [HttpGet]
    public IActionResult Get() => Ok(Current());

    [HttpPatch]
    public IActionResult Patch([FromBody] SettingsPatch patch)
    {
        if (patch.EngramTurns is < 0) return BadRequest(new { error = "Engram turns can't be negative." });
        if (patch.ReasoningEffort is < 0 or > 2) return BadRequest(new { error = "Reasoning effort is 0 (low), 1 (medium) or 2 (xhigh)." });
        if (patch.Logging is { RetentionDays: < 0 }) return BadRequest(new { error = "Retention days can't be negative." });
        if (patch.DreamingEnabled is not null && Modules.Llm is null) return StatusCode(503, new { error = "LLM is not available." });

        AgentDefinition? engram = null;
        if (patch.EngramTurns is not null)
        {
            engram = persistentData.GetAgent("Engram");
            if (engram is null) return NotFound(new { error = "Engram agent definition not found." });
        }

        bool restartRequired = false;
        if (patch.Logging is not null)
        {
            try { WriteLogging(patch.Logging); restartRequired = true; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Settings] Failed to write {Path}.", Paths.AriConfig);
                return StatusCode(500, new { error = $"Could not save AriConfig.json: {ex.Message}" });
            }
        }

        if (patch.UserName       is not null) UserNameStore.Set(patch.UserName);
        if (patch.Conventions    is not null) ConventionsStore.Set(patch.Conventions);
        if (patch.SafeModePrompt is not null) SafeModePromptStore.Set(patch.SafeModePrompt);
        if (patch.Persona        is not null) PersonaStore.Set(patch.Persona);
        if (patch.PersistedThreadsEnabled is { } persisted) PersistedThreadsSetting.Set(persisted);
        if (patch.EngramOthers is { } others) EngramOthersSetting.Set(others);
        if (patch.ReasoningEffort is { } effort) ReasoningEffortStore.Step = effort;
        if (patch.DreamingEnabled is { } dreaming) Modules.Llm!.DreamingEnabled = dreaming;
        if (engram is not null)
        {
            // Saved to Agents.json AND applied to the running Engram, so no restart is needed.
            engram.TurnsBeforeSweep = patch.EngramTurns!.Value;
            persistentData.UpdateAgent(engram);
            Llm?.SetEngramTurnInterval(patch.EngramTurns.Value);
        }

        return Ok(new { settings = Current(), restartRequired });
    }

    private object Current()
    {
        JsonNode? rec = ReadRoot()?["Recording"];
        return new
        {
            userName       = UserNameStore.Get(),
            conventions    = ConventionsStore.Get(),
            safeModePrompt = SafeModePromptStore.Get(),
            persona        = PersonaStore.Get(),
            dreamingEnabled = Modules.Llm?.DreamingEnabled ?? false,
            // The one global dial for how hard ARI thinks: 0/1/2 = low/medium/xhigh. Anyone signed in can read it at /reasoningEffort.
            reasoningEffort = ReasoningEffortStore.Step,
            // Whether closed conversations are kept so they can be reopened later. The saved ones are at /admin/persistedThreads.
            persistedThreadsEnabled = PersistedThreadsSetting.Enabled,
            // Whether Engram may remember conversations that are not the Master's own: server channels, DMs with other people, guests.
            engramOthers = EngramOthersSetting.Enabled,
            // How many completed exchanges a thread builds up before Engram sweeps it mid-conversation. 0 = off.
            engramTurns    = Llm?.GetEngramTurnInterval() ?? persistentData.GetAgent("Engram")?.TurnsBeforeSweep ?? 5,
            // The session recorder (DTI, dream and chat logs): what it keeps and for how long. Read when ARI starts.
            logging = new
            {
                enabled             = rec?["Enabled"]?.GetValue<bool>()             ?? true,
                includeFullRequests = rec?["IncludeFullRequests"]?.GetValue<bool>() ?? true,
                includeToolResults  = rec?["IncludeToolResults"]?.GetValue<bool>()  ?? true,
                retentionDays       = rec?["RetentionDays"]?.GetValue<int>()        ?? 0,
            },
        };
    }

    private static void WriteLogging(LoggingSettings logging)
    {
        JsonObject root = ReadRoot() ?? throw new InvalidOperationException("AriConfig.json is missing.");
        if (root["Recording"] is not JsonObject rec) root["Recording"] = rec = new JsonObject();
        rec["Enabled"]             = logging.Enabled;
        rec["IncludeFullRequests"] = logging.IncludeFullRequests;
        rec["IncludeToolResults"]  = logging.IncludeToolResults;
        rec["RetentionDays"]       = logging.RetentionDays;
        ConfigFile.Write(Paths.AriConfig, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject? ReadRoot()
    {
        if (!System.IO.File.Exists(Paths.AriConfig)) return null;
        return JsonNode.Parse(System.IO.File.ReadAllText(Paths.AriConfig),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
    }
}

using ARI.Common;
using ARI.ImageGen;
using ARI.Voice;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARI.API.Controllers;

/// <summary>Voice and image generation: whether each loads when first needed (and unloads after sitting idle) or loads when ARI starts
/// and stays loaded, what is loaded now, and the idle time. An idle time of 0 in the config means "stays loaded". Changes are saved to the
/// config and apply when ARI next starts.</summary>
[Route("api/v1/admin/lazyServices")]
[ApiController]
public class LazyServicesController : ControllerBase
{
    private static JsonNode? ModuleNode(JsonNode root, string name)
    {
        JsonNode? modules = root["Modules"] ?? root["modules"];
        return modules?[name] ?? modules?[name.ToLowerInvariant()];
    }

    [HttpGet]
    public IActionResult Get()
    {
        JsonNode? root = System.IO.File.Exists(Paths.AriConfig) ? JsonNode.Parse(System.IO.File.ReadAllText(Paths.AriConfig)) : null;
        JsonNode? voice = root is null ? null : ModuleNode(root, "Voice"), image = root is null ? null : ModuleNode(root, "ImageGen");
        int voiceMinutes = voice?["IdleStopMinutes"]?.GetValue<int>() ?? new VoiceConfig().IdleStopMinutes;
        int imageSeconds = image?["IdleSeconds"]?.GetValue<int>() ?? 600;

        bool voiceOn = Modules.Voice is not null;
        bool voiceRunning = (Modules.Voice as VoiceModule)?.Synthesiser is VoiceModuleSynthesiser s && s.IsRunning;
        bool imageOn = Modules.ImageGen is not null;
        bool imageRunning = (Modules.ImageGen as ImageGenModule)?.IsRunning ?? false;

        // 0 means stays loaded; the idle box still shows a sensible time for when it's switched back to load-when-needed.
        return Ok(new[]
        {
            new { id = "voice", name = "Voice", enabled = voiceOn, running = voiceRunning, onDemand = voiceMinutes > 0,
                  idleMinutes = voiceMinutes > 0 ? voiceMinutes : 10, appliesAt = "restart" },
            new { id = "image", name = "Image generation", enabled = imageOn, running = imageRunning, onDemand = imageSeconds > 0,
                  idleMinutes = imageSeconds > 0 ? Math.Max(1, imageSeconds / 60) : 10, appliesAt = "restart" },
        });
    }

    /// <param name="OnDemand">False: load at startup and stay loaded. True or missing: load when first needed and unload after IdleMinutes.</param>
    public sealed record IdleRequest(string Id, int IdleMinutes, bool? OnDemand = null);

    [HttpPut]
    public IActionResult Put([FromBody] IdleRequest req)
    {
        bool stayLoaded = req.OnDemand == false;
        if (!stayLoaded && req.IdleMinutes is < 1 or > 1440) return BadRequest(new { error = "Choose between 1 minute and 24 hours." });
        int minutes = stayLoaded ? 0 : req.IdleMinutes;
        if (!System.IO.File.Exists(Paths.AriConfig)) return NotFound();
        JsonNode root = JsonNode.Parse(System.IO.File.ReadAllText(Paths.AriConfig))!;
        JsonNode modules = root["Modules"] ?? root["modules"] ?? throw new Exception("AriConfig.json has no Modules section.");
        switch (req.Id)
        {
            case "voice":
                if (ModuleNode(root, "Voice") is not JsonObject v) return NotFound();
                v["IdleStopMinutes"] = minutes; break;
            case "image":
                if (ModuleNode(root, "ImageGen") is not JsonObject i) return NotFound();
                i["IdleSeconds"] = minutes * 60; break;
            default: return BadRequest(new { error = "Unknown service." });
        }
        ConfigFile.Write(Paths.AriConfig, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return Ok(new { saved = true, appliesAt = "restart" });
    }
}

using ARI.Common;
using ARI.ImageGen;
using ARI.Voice;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARI.API.Controllers;

/// <summary>The services that load when first used and unload after sitting idle (voice, image generation): what is loaded now and how long
/// each may sit idle before it is unloaded. Changing a time saves it to the config; voice picks it up when ARI next starts.</summary>
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

        return Ok(new[]
        {
            new { id = "voice", name = "Voice", enabled = voiceOn, running = voiceRunning, idleMinutes = voiceMinutes, appliesAt = "restart" },
            new { id = "image", name = "Image generation", enabled = imageOn, running = imageRunning, idleMinutes = imageSeconds / 60, appliesAt = "restart" },
        });
    }

    public sealed record IdleRequest(string Id, int IdleMinutes);

    [HttpPut]
    public IActionResult Put([FromBody] IdleRequest req)
    {
        if (req.IdleMinutes is < 1 or > 1440) return BadRequest(new { error = "Choose between 1 minute and 24 hours." });
        if (!System.IO.File.Exists(Paths.AriConfig)) return NotFound();
        JsonNode root = JsonNode.Parse(System.IO.File.ReadAllText(Paths.AriConfig))!;
        JsonNode modules = root["Modules"] ?? root["modules"] ?? throw new Exception("AriConfig.json has no Modules section.");
        switch (req.Id)
        {
            case "voice":
                if (ModuleNode(root, "Voice") is not JsonObject v) return NotFound();
                v["IdleStopMinutes"] = req.IdleMinutes; break;
            case "image":
                if (ModuleNode(root, "ImageGen") is not JsonObject i) return NotFound();
                i["IdleSeconds"] = req.IdleMinutes * 60; break;
            default: return BadRequest(new { error = "Unknown service." });
        }
        ConfigFile.Write(Paths.AriConfig, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return Ok(new { saved = true, appliesAt = "restart" });
    }
}

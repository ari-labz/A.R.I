using ARI.API.Data;
using ARI.Common;
using ARI.ImageGen;
using ARI.LLM;
using ARI.Voice;
using ARI.VoiceSynthesis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ARI.API.Controllers;

// ── Image Generation API ──────────────────────────────────────────────────────

[Route("api/v1/admin/imagegen")]
[ApiController]
public class ImageGenController(APIConfig config) : ControllerBase
{
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        string path = ARI.Common.Paths.AriConfig;
        if (!System.IO.File.Exists(path)) return NotFound();

        using JsonDocument doc  = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        JsonElement        root = doc.RootElement;

        if (!root.TryGetProperty("Modules", out JsonElement modules) &&
            !root.TryGetProperty("modules", out modules))
            return Ok(new { Enabled = false, Checkpoint = "", Port = 8188, IdleSeconds = 300 });

        if (!modules.TryGetProperty("ImageGen", out JsonElement ig) &&
            !modules.TryGetProperty("imagegen", out ig))
            return Ok(new { Enabled = false, Checkpoint = "", Port = 8188, IdleSeconds = 300 });

        return Ok(new
        {
            Enabled     = ig.TryGetProperty("Enabled",     out JsonElement e)   && e.GetBoolean(),
            Checkpoint  = ig.TryGetProperty("Checkpoint",  out JsonElement c)   ? c.GetString()  ?? "" : "",
            ComfyUiPath = ig.TryGetProperty("ComfyUiPath", out JsonElement cup) ? cup.GetString() ?? "" : "",
            Port        = ig.TryGetProperty("Port",        out JsonElement p)   ? p.GetInt32()        : 8188,
            IdleSeconds = ig.TryGetProperty("IdleSeconds", out JsonElement id)  ? id.GetInt32()       : 300,
        });
    }

    [HttpPatch("config")]
    public IActionResult SaveConfig([FromBody] ImageGenConfigRequest req)
    {
        string path = ARI.Common.Paths.AriConfig;
        if (!System.IO.File.Exists(path)) return NotFound();

        JsonNode root = JsonNode.Parse(System.IO.File.ReadAllText(path))!;
        JsonNode modules = root["Modules"] ?? root["modules"]
            ?? throw new Exception("AriConfig.json has no Modules section.");

        JsonNode ig = modules["ImageGen"] ?? modules["imagegen"] ?? new JsonObject();
        // Only what was sent changes.
        if (req.Enabled     is { } enabled)     ig["Enabled"]     = enabled;
        if (req.Checkpoint  is { } checkpoint)  ig["Checkpoint"]  = checkpoint;
        if (req.ComfyUiPath is { } comfyUiPath) ig["ComfyUiPath"] = comfyUiPath;
        if (req.Port        is { } port)        ig["Port"]        = port;
        if (req.IdleSeconds is { } idleSeconds) ig["IdleSeconds"] = idleSeconds;

        // Write back under whichever key exists
        if (modules["ImageGen"] is not null) modules["ImageGen"] = ig;
        else if (modules["imagegen"] is not null) modules["imagegen"] = ig;
        else modules["ImageGen"] = ig;

        System.IO.File.WriteAllText(path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return Ok();
    }

    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(new
    {
        Ready  = Modules.ImageGen?.IsReady ?? false,
        Active = Modules.ImageGen is not null,
    });

    [HttpGet("models")]
    public IActionResult ListCheckpoints()
    {
        // Walk ComfyUI's models/checkpoints directory and return filenames.
        string comfyPath = Modules.ImageGen is not null
            ? ARI.ImageGen.Dependency.ComfyUiPath ?? ARI.ImageGen.Dependency.DefaultInstallPath()
            : ARI.ImageGen.Dependency.DefaultInstallPath();

        string ckptDir = Path.Combine(comfyPath, "models", "checkpoints");
        if (!Directory.Exists(ckptDir))
            return Ok(Array.Empty<string>());

        string[] files = Directory.GetFiles(ckptDir, "*.safetensors")
            .Concat(Directory.GetFiles(ckptDir, "*.ckpt"))
            .Select(Path.GetFileName)
            .Where(f => f is not null)
            .OrderBy(f => f)
            .ToArray()!;

        return Ok(files);
    }
}

public record ImageGenConfigRequest(bool? Enabled, string? Checkpoint, string? ComfyUiPath, int? Port, int? IdleSeconds);

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

[Route("api/v1/admin/imageGen")]
[ApiController]
public class ImageGenController(APIConfig config) : ControllerBase
{
    private static readonly HttpClient ComfyHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static ImageGenConfig? Live => (Modules.ImageGen as ImageGenModule)?.Config;

    private static JsonNode? ImageGenNode(JsonNode root)
    {
        JsonNode? modules = root["Modules"] ?? root["modules"];
        return modules?["ImageGen"] ?? modules?["imagegen"];
    }

    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        string path = ARI.Common.Paths.AriConfig;
        if (!System.IO.File.Exists(path)) return NotFound();

        JsonNode  root = JsonNode.Parse(System.IO.File.ReadAllText(path))!;
        JsonNode? ig   = ImageGenNode(root);
        T Get<T>(string key, T fallback) { try { return ig?[key] is { } n ? n.GetValue<T>() : fallback; } catch { return fallback; } }

        return Ok(new
        {
            Enabled     = Get("Enabled", false),
            Checkpoint  = Get("Checkpoint", ""),
            ComfyUiPath = Get("ComfyUiPath", ""),
            Port        = Get("Port", 8188),
            IdleSeconds = Get("IdleSeconds", 300),
            RemoteUrl   = Get("RemoteUrl", ""),
            Sampler     = Get("Sampler", "euler"),
            Scheduler   = Get("Scheduler", "normal"),
            Cfg         = Get("Cfg", 7.0),
            Steps       = Get("Steps", 25),
            Width       = Get("Width", 1024),
            Height      = Get("Height", 1024),
        });
    }

    [HttpPatch("config")]
    public IActionResult SaveConfig([FromBody] ImageGenConfigRequest req)
    {
        if (req.RemoteUrl is { Length: > 0 } url && !(Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? u) && u.Scheme is "http" or "https"))
            return BadRequest(new { error = "The ComfyUI address should start with http:// or https://." });
        if (req.Width is < 64 or > 4096 || req.Height is < 64 or > 4096 || req.Steps is < 1 or > 150 || req.Cfg is < 0 or > 30)
            return BadRequest(new { error = "One of the drawing defaults is out of range." });

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
        if (req.RemoteUrl   is { } remoteUrl)   ig["RemoteUrl"]   = remoteUrl.Trim();
        if (req.Sampler     is { } sampler)     ig["Sampler"]     = sampler;
        if (req.Scheduler   is { } scheduler)   ig["Scheduler"]   = scheduler;
        if (req.Cfg         is { } cfg)         ig["Cfg"]         = cfg;
        if (req.Steps       is { } steps)       ig["Steps"]       = steps;
        if (req.Width       is { } width)       ig["Width"]       = width;
        if (req.Height      is { } height)      ig["Height"]      = height;

        // Write back under whichever key exists
        if (modules["ImageGen"] is not null) modules["ImageGen"] = ig;
        else if (modules["imagegen"] is not null) modules["imagegen"] = ig;
        else modules["ImageGen"] = ig;

        ConfigFile.Write(path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // The model and the drawing defaults apply to the next image; the server settings wait for a restart.
        if (Live is { } live)
        {
            if (req.Checkpoint is { } c)  live.Checkpoint = c;
            if (req.Sampler    is { } sm) live.Sampler    = sm;
            if (req.Scheduler  is { } sc) live.Scheduler  = sc;
            if (req.Cfg        is { } cf) live.Cfg        = cf;
            if (req.Steps      is { } st) live.Steps      = st;
            if (req.Width      is { } w)  live.Width      = w;
            if (req.Height     is { } h)  live.Height     = h;
            if (req.RemoteUrl  is { } ru) live.RemoteUrl  = ru.Trim();
        }

        bool serverChanged = req.ComfyUiPath is not null || req.Port is not null || req.IdleSeconds is not null || req.RemoteUrl is not null;
        return Ok(new { restartRequired = serverChanged });
    }

    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(new
    {
        Ready  = Modules.ImageGen?.IsReady ?? false,
        Active = Modules.ImageGen is not null,
        Remote = Live?.IsRemote ?? false,
    });

    /// <summary>The checkpoints the server can draw with: the files in the managed ComfyUI's folder, or whatever a
    /// remote ComfyUI says it has.</summary>
    [HttpGet("models")]
    public async Task<IActionResult> ListCheckpoints(CancellationToken ct)
    {
        string active = Live?.Checkpoint ?? ConfiguredCheckpoint();
        string? remoteUrl = Live is { IsRemote: true } l ? l.RemoteUrl.Trim().TrimEnd('/') : null;

        if (remoteUrl is not null)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(await ComfyHttp.GetStringAsync($"{remoteUrl}/object_info/CheckpointLoaderSimple", ct));
                JsonElement names = doc.RootElement.GetProperty("CheckpointLoaderSimple").GetProperty("input").GetProperty("required").GetProperty("ckpt_name")[0];
                return Ok(new { remote = true, active, checkpoints = names.EnumerateArray().Select(n => new { name = n.GetString(), size = (long?)null }) });
            }
            catch (Exception e)
            {
                return Ok(new { remote = true, active, checkpoints = Array.Empty<object>(), error = $"Couldn't reach ComfyUI at {remoteUrl}: {e.Message}" });
            }
        }

        string dir = CheckpointDownloads.CheckpointsDir;
        var local = Directory.Exists(dir)
            ? Directory.GetFiles(dir).Where(f => f.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ckpt", StringComparison.OrdinalIgnoreCase))
                .Select(f => new { name = Path.GetFileName(f), size = (long?)new FileInfo(f).Length }).OrderBy(f => f.name).ToArray()
            : [];
        return Ok(new { remote = false, active, checkpoints = local });
    }

    // The saved choice, for when the module isn't running to ask.
    private static string ConfiguredCheckpoint()
    {
        try { return ImageGenNode(JsonNode.Parse(System.IO.File.ReadAllText(ARI.Common.Paths.AriConfig))!)?["Checkpoint"]?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }

    [HttpGet("models/search")]
    public async Task<IActionResult> Search([FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) return Ok(new { repos = Array.Empty<string>() });
        // A pasted file link goes straight to its repo.
        if (CheckpointDownloads.ParseLink(q) is { } link) return Ok(new { repos = new[] { link.Repo }, link = new { repo = link.Repo, file = link.File } });
        try { return Ok(new { repos = await CheckpointDownloads.Search(q.Trim(), ct) }); }
        catch (Exception e) { return StatusCode(502, new { error = $"Couldn't search Hugging Face: {e.Message}" }); }
    }

    [HttpGet("models/files")]
    public async Task<IActionResult> RepoFiles([FromQuery] string repo, CancellationToken ct)
    {
        try { return Ok(new { files = await CheckpointDownloads.Files(repo, ct) }); }
        catch (ArgumentException e) { return BadRequest(new { error = e.Message }); }
        catch (Exception e) { return StatusCode(502, new { error = $"Couldn't read that repo: {e.Message}" }); }
    }

    public sealed record DownloadRequest(string? Repo, string? File, string? Link);

    [HttpPost("models/download")]
    public IActionResult StartDownload([FromBody] DownloadRequest req)
    {
        if (Live is { IsRemote: true }) return BadRequest(new { error = "Models can't be downloaded to a remote ComfyUI. Put them on that machine." });
        (string Repo, string File)? parsed = req.Link is { Length: > 0 } ? CheckpointDownloads.ParseLink(req.Link) : null;
        string repo = parsed?.Repo ?? req.Repo ?? "", file = parsed?.File ?? req.File ?? "";
        string? error = CheckpointDownloads.Start(repo, file);
        return error is null ? Ok(new { ok = true }) : BadRequest(new { error });
    }

    [HttpGet("models/download")]
    public IActionResult DownloadStatus() => Ok(new { download = CheckpointDownloads.Current });

    [HttpDelete("models/download")]
    public IActionResult CancelDownload() { CheckpointDownloads.Cancel(); return Ok(new { ok = true }); }

    [HttpDelete("models/file/{name}")]
    public IActionResult DeleteCheckpoint(string name)
    {
        if (Live is { IsRemote: true }) return BadRequest(new { error = "Models on a remote ComfyUI can't be removed from here." });
        if (name != Path.GetFileName(name)) return BadRequest(new { error = "That isn't a model file name." });
        if (name == (Live?.Checkpoint ?? ConfiguredCheckpoint())) return BadRequest(new { error = "That model is the active one. Pick another first." });
        string path = Path.Combine(CheckpointDownloads.CheckpointsDir, name);
        if (!System.IO.File.Exists(path)) return NotFound(new { error = "No such model." });
        System.IO.File.Delete(path);
        return Ok(new { ok = true });
    }
}

public record ImageGenConfigRequest(bool? Enabled, string? Checkpoint, string? ComfyUiPath, int? Port, int? IdleSeconds,
                                    string? RemoteUrl, string? Sampler, string? Scheduler, double? Cfg, int? Steps, int? Width, int? Height);

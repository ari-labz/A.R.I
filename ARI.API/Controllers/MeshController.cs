using ARI.Common;
using ARI.Mesh;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARI.API.Controllers;

// ── Mesh Generation API ───────────────────────────────────────────────────────
// The panel talks to ARI; ARI talks to MeshServer, which owns the 3D models, their downloads and the memory it can see.

[Route("api/v1/admin/mesh")]
[ApiController]
public class MeshController : ControllerBase
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static MeshConfig? Live => (Modules.Mesh as MeshModule)?.Config;

    private static JsonNode? MeshNode(JsonNode root)
    {
        JsonNode? modules = root["Modules"] ?? root["modules"];
        return modules?["Mesh"] ?? modules?["mesh"];
    }

    private static string ServerUrl()
    {
        if (Live is { } l) return l.ServerUrl.Trim().TrimEnd('/');
        try { return (MeshNode(JsonNode.Parse(System.IO.File.ReadAllText(Paths.AriConfig))!)?["ServerUrl"]?.GetValue<string>() ?? "").Trim().TrimEnd('/'); }
        catch { return ""; }
    }

    private static bool IsSameMachine(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && (u.IsLoopback || u.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase));

    private async Task<IActionResult> Forward(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        string url = ServerUrl();
        if (url.Length == 0) return BadRequest(new { error = "Set the mesh server address in Settings first." });
        try
        {
            using HttpRequestMessage req = new(method, url + path);
            if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using HttpResponseMessage r = await Http.SendAsync(req, ct);
            return new ContentResult { Content = await r.Content.ReadAsStringAsync(ct), ContentType = "application/json", StatusCode = (int)r.StatusCode };
        }
        catch (Exception e) { return StatusCode(502, new { error = $"Couldn't reach the mesh server at {url}: {e.Message}" }); }
    }

    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        string path = Paths.AriConfig;
        if (!System.IO.File.Exists(path)) return NotFound();
        JsonNode? m = MeshNode(JsonNode.Parse(System.IO.File.ReadAllText(path))!);
        T Get<T>(string key, T fallback) { try { return m?[key] is { } n ? n.GetValue<T>() : fallback; } catch { return fallback; } }
        return Ok(new
        {
            Enabled           = Get("Enabled", false),
            ServerUrl         = Get("ServerUrl", "http://127.0.0.1:8199"),
            StopLlmsDuringJob = Get("StopLlmsDuringJob", true),
            MaxFaces          = Get("MaxFaces", 0),
        });
    }

    public sealed record MeshConfigRequest(bool? Enabled, string? ServerUrl, bool? StopLlmsDuringJob, int? MaxFaces);

    [HttpPatch("config")]
    public IActionResult SaveConfig([FromBody] MeshConfigRequest req)
    {
        if (req.ServerUrl is { Length: > 0 } url && !(Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? u) && u.Scheme is "http" or "https"))
            return BadRequest(new { error = "The mesh server address should start with http:// or https://." });
        if (req.MaxFaces is < 0 or > 200000 or (> 0 and < 100)) return BadRequest(new { error = "The triangle limit should be 0 (no decimation) or between 100 and 200000." });

        string path = Paths.AriConfig;
        if (!System.IO.File.Exists(path)) return NotFound();
        JsonNode root = JsonNode.Parse(System.IO.File.ReadAllText(path))!;
        JsonNode modules = root["Modules"] ?? root["modules"] ?? throw new Exception("AriConfig.json has no Modules section.");
        JsonNode m = modules["Mesh"] ?? modules["mesh"] ?? new JsonObject();
        if (req.Enabled           is { } e)  m["Enabled"]           = e;
        if (req.ServerUrl         is { } su) m["ServerUrl"]         = su.Trim();
        if (req.StopLlmsDuringJob is { } sl) m["StopLlmsDuringJob"] = sl;
        if (req.MaxFaces          is { } mf) m["MaxFaces"]          = mf;
        if (modules["mesh"] is not null) modules["mesh"] = m; else modules["Mesh"] = m;
        ConfigFile.Write(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // Applies to the next job; nothing here needs a restart.
        if (Live is { } live)
        {
            if (req.ServerUrl         is { } a) live.ServerUrl         = a.Trim();
            if (req.StopLlmsDuringJob is { } b) live.StopLlmsDuringJob = b;
            if (req.MaxFaces          is { } c) live.MaxFaces          = c;
        }
        return Ok(new { ok = true });
    }

    /// <summary>The server's own health and memory, plus whether it sits on this machine (stopping the language models only
    /// helps then). Reachable false means nothing answered at the address.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        string url = ServerUrl();
        bool same = IsSameMachine(url);
        try
        {
            string json = url.Length == 0 ? throw new Exception("No address set.") : await Http.GetStringAsync(url + "/health", ct);
            return Ok(new { active = Modules.Mesh is not null, reachable = true, sameMachine = same, reservedGb = SystemMemory.ReservedGb, server = JsonNode.Parse(json) });
        }
        catch (Exception e)
        {
            return Ok(new { active = Modules.Mesh is not null, reachable = false, sameMachine = same, reservedGb = SystemMemory.ReservedGb, error = e.Message });
        }
    }

    [HttpGet("models")]
    public Task<IActionResult> Models(CancellationToken ct) => Forward(HttpMethod.Get, "/models", null, ct);

    public sealed record ModelRequest(string? Id);

    [HttpPost("models/select")]
    public Task<IActionResult> Select([FromBody] ModelRequest req, CancellationToken ct) =>
        Forward(HttpMethod.Post, "/models/select", JsonSerializer.Serialize(new { id = req.Id }), ct);

    [HttpPost("models/download")]
    public Task<IActionResult> Download([FromBody] ModelRequest req, CancellationToken ct) =>
        Forward(HttpMethod.Post, "/models/download", JsonSerializer.Serialize(new { id = req.Id }), ct);

    [HttpDelete("models/{id}")]
    public Task<IActionResult> Delete(string id, CancellationToken ct) =>
        Forward(HttpMethod.Delete, "/models/" + Uri.EscapeDataString(id), null, ct);
}

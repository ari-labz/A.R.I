using System.Text.Json;
using System.Text.Json.Nodes;
using ARI.API.Data;
using ARI.Common;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>
/// What the servers page needs that no single server knows: a server's llama-server log and saved sampler profiles. Admin only (under the admin route).
/// </summary>
[ApiController]
public class MachineController(PersistentData persistentData) : ControllerBase
{
    /// <summary>The end of a server's llama-server log, as text. <c>?lines=</c> is how many (default 300, at most 2000).</summary>
    [HttpGet("api/v1/admin/servers/{id:guid}/logs")]
    public IActionResult Logs(Guid id, [FromQuery] int lines = 300)
    {
        ARI.LLM.Server? server = persistentData.GetServer(id);
        if (server is null) return NotFound(new { error = "Server not found." });
        string file = Path.Combine(Paths.Logs, $"llama-{server.Name}.log");
        if (!System.IO.File.Exists(file)) return Ok(new { text = "", note = "No log yet. It is written when the server starts." });
        try
        {
            using FileStream fs = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(fs);
            Queue<string> tail = new();
            int keep = Math.Clamp(lines, 10, 2000);
            string? line;
            while ((line = reader.ReadLine()) is not null) { tail.Enqueue(line); if (tail.Count > keep) tail.Dequeue(); }
            return Ok(new { text = string.Join('\n', tail) });
        }
        catch (IOException) { return Ok(new { text = "", note = "The log is busy; try again." }); }
    }

    // ── Sampler profiles: named sets of sampler values that can be applied to any server ──

    private static string ProfilesPath => Path.Combine(Paths.PersistentData, "SamplerProfiles.json");
    private static readonly object ProfilesLock = new();

    private static JsonObject LoadProfiles()
    {
        try { if (System.IO.File.Exists(ProfilesPath)) return JsonNode.Parse(System.IO.File.ReadAllText(ProfilesPath)) as JsonObject ?? new(); }
        catch { }
        return new();
    }

    [HttpGet("api/v1/admin/sampler-profiles")]
    public IActionResult ListProfiles() { lock (ProfilesLock) return Ok(LoadProfiles()); }

    /// <summary>Saves (or replaces) a profile: the body is the sampler values as a JSON object.</summary>
    [HttpPut("api/v1/admin/sampler-profiles/{name}")]
    public IActionResult SaveProfile(string name, [FromBody] JsonObject values)
    {
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { error = "Give the profile a name." });
        lock (ProfilesLock)
        {
            JsonObject all = LoadProfiles();
            all[name.Trim()] = values.DeepClone();
            System.IO.File.WriteAllText(ProfilesPath, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        return Ok();
    }

    [HttpDelete("api/v1/admin/sampler-profiles/{name}")]
    public IActionResult DeleteProfile(string name)
    {
        lock (ProfilesLock)
        {
            JsonObject all = LoadProfiles();
            if (!all.Remove(name)) return NotFound();
            System.IO.File.WriteAllText(ProfilesPath, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        return Ok();
    }
}

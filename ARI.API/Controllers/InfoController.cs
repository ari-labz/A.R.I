using System.Text.Json;
using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

[Route("api/info")]
[ApiController]
public class InfoController : ControllerBase
{
    // Bump this whenever a breaking change requires clients to update.
    private const string RequiredClientVersion = "0.6.0";

    private static LLMModule? Llm => (LLMModule?)Modules.Llm;

    private static int? _protocol;
    private static int? ReadProtocol()
    {
        if (_protocol.HasValue) return _protocol;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "manifest.json");
            if (!File.Exists(path)) return null;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("protocol", out JsonElement p) && p.ValueKind == JsonValueKind.Number)
                _protocol = p.GetInt32();
        }
        catch { }
        return _protocol;
    }

    /// <summary>
    /// Whether any model server is online, so the client can say so rather than let someone type into a
    /// composer that cannot answer. On a fresh install nothing is running by design — the demo server
    /// ships with bootStartup off so the user picks their model before anything is downloaded.
    /// </summary>
    [HttpGet("ready")]
    public IActionResult GetReady()
    {
        IReadOnlyList<Server> servers = Llm?.Servers ?? [];
        return Ok(new
        {
            ready         = servers.Any(s => s.Status == ServerStatus.Online),
            starting      = servers.Any(s => s.Status == ServerStatus.Starting),
            serverCount   = servers.Count,
            protocol      = ReadProtocol(),
        });
    }

    [HttpGet("version")]
    public async Task<IActionResult> GetVersion()
    {
        await UpdateCheck.EnsureCheckedAsync();
        return Ok(new
        {
            requiredClientVersion = RequiredClientVersion,
            serverVersion         = UpdateCheck.ServerVersion,
            latestVersion         = UpdateCheck.LatestVersion,
            serverOutdated        = UpdateCheck.Outdated,
        });
    }
}

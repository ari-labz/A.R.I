using ARI.LLM;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>The MCP servers ARI runs (McpServers.json): the list as text to edit, each server's state and tools, and a reload.</summary>
[Route("api/v1/admin/mcp")]
[ApiController]
public class McpController : ControllerBase
{
    public sealed record Body(string Config);

    [HttpGet]
    public IActionResult Get() => Ok(new { config = McpTools.ReadConfig(), servers = McpTools.Status() });

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] Body body)
    {
        if (McpTools.SaveConfig(body.Config ?? "") is { } error) return BadRequest(new { error });
        await McpTools.ReloadAsync();
        return Ok(new { servers = McpTools.Status() });
    }

    [HttpPost("reload")]
    public async Task<IActionResult> Reload()
    {
        await McpTools.ReloadAsync();
        return Ok(new { servers = McpTools.Status() });
    }
}

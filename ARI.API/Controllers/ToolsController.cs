using System.Text.RegularExpressions;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>The Tools tab: every tool with its group, whether it is switched on, and whether it runs async (the
/// model carries on while it works and gets the result later). Changes apply to the next tool load and the next
/// call; see <see cref="ToolRegistry"/>.</summary>
[Route("api/v1/admin/tools")]
[ApiController]
public class ToolsController : ControllerBase
{
    public sealed record GroupPatch(bool Enabled);

    [HttpGet]
    public IActionResult Get()
    {
        (List<ToolInfo> tools, List<ToolGroupInfo> groups) = ToolRegistry.Catalogue();
        return Ok(new { tools, groups });
    }

    [HttpPatch("{name}")]
    public IActionResult Patch(string name, [FromBody] ToolPatch? body)
    {
        if (body is null) return BadRequest(new { error = "Nothing to change." });
        if (body.Group is { } g && g != ToolRegistry.DefaultGroup && g.Length > 0 && !Regex.IsMatch(g, @"^[a-z0-9_]{1,40}$"))
            return BadRequest(new { error = "Group names use lowercase letters, numbers and _ (up to 40 characters)." });
        return ToolRegistry.Update(name, body) ? Ok(new { ok = true }) : NotFound(new { error = $"No tool called '{name}'." });
    }

    [HttpPatch("groups/{group}")]
    public IActionResult PatchGroup(string group, [FromBody] GroupPatch? body)
        => body is not null && ToolRegistry.SetGroupEnabled(group, body.Enabled)
            ? Ok(new { ok = true })
            : NotFound(new { error = $"No group called '{group}'." });
}

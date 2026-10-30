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

    public sealed record ShellSettingsBody(bool AutoApprove, List<string>? Allow);

    /// <summary>The shell's approval setting: off asks the owner before anything that isn't read-only; on runs everything.</summary>
    [HttpGet("shell-settings")]
    public IActionResult GetShellSettings() => Ok(new { autoApprove = ShellGateAccess.AutoApprove, allow = ShellGateAccess.AllowList });

    [HttpPut("shell-settings")]
    public IActionResult SetShellSettings([FromBody] ShellSettingsBody? body)
    {
        if (body is null) return BadRequest(new { error = "Nothing to change." });
        ShellGateAccess.Save(body.AutoApprove, body.Allow ?? ShellGateAccess.AllowList);
        return Ok(new { autoApprove = ShellGateAccess.AutoApprove, allow = ShellGateAccess.AllowList });
    }

    public sealed record BulkPatch(List<string>? Names, ToolPatch? Patch);

    /// <summary>One change applied to many tools: switch them on or off, set async, or move them to a group.</summary>
    [HttpPatch]
    public IActionResult PatchMany([FromBody] BulkPatch? body)
    {
        if (body?.Names is not { Count: > 0 } || body.Patch is null) return BadRequest(new { error = "Choose some tools and a change." });
        if (body.Patch.Group is { } g && g != ToolRegistry.DefaultGroup && g.Length > 0 && !Regex.IsMatch(g, @"^[a-z0-9_]{1,40}$"))
            return BadRequest(new { error = "Group names use lowercase letters, numbers and _ (up to 40 characters)." });
        return Ok(new { changed = ToolRegistry.UpdateMany(body.Names, body.Patch) });
    }

    [HttpGet("{name}/detail")]
    public IActionResult Detail(string name)
        => ToolRegistry.Detail(name) is { } d ? Ok(d) : NotFound(new { error = $"No tool called '{name}'." });

    /// <summary>A custom-tool starting point made from a built-in tool. Nothing is saved until the admin saves it.</summary>
    [HttpPost("{name}/fork")]
    public IActionResult Fork(string name)
        => CustomTools.Fork(name) is { } f ? Ok(new { file = f.File, code = f.Code }) : NotFound(new { error = $"No tool called '{name}'." });

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

    // ── Custom tools: C# scripts the admin writes or drops in (see ARI.LLM CustomTools) ──────────────────────

    public sealed record CodeBody(string? Code);
    public sealed record TestBody(string? Name, string? Args);

    private const int MaxScriptChars = 200_000;

    [HttpGet("custom")]
    public IActionResult GetCustom() => Ok(new
    {
        tools     = CustomTools.List(),
        templates = CustomTools.Templates.Select(t => new { name = t.Key, code = t.Value }),
    });

    [HttpGet("custom/{file}")]
    public IActionResult GetCustomCode(string file)
        => CustomTools.ReadCode(file) is { } code ? Ok(new { code }) : NotFound(new { error = "No such script." });

    /// <summary>Saves and compiles a script. A script that doesn't compile is still saved (so nothing typed is lost);
    /// the response says so, and the last version that did compile keeps running.</summary>
    [HttpPut("custom/{file}")]
    public IActionResult SaveCustom(string file, [FromBody] CodeBody? body)
    {
        if (string.IsNullOrWhiteSpace(body?.Code)) return BadRequest(new { error = "The script is empty." });
        if (body.Code.Length > MaxScriptChars) return BadRequest(new { error = "That script is too long." });
        try { return Ok(CustomTools.Save(file, body.Code)); }
        catch (ArgumentException e) { return BadRequest(new { error = e.Message }); }
    }

    [HttpDelete("custom/{file}")]
    public IActionResult DeleteCustom(string file)
        => CustomTools.Delete(file) ? Ok(new { ok = true }) : NotFound(new { error = "No such script." });

    /// <summary>Calls a custom tool once with JSON arguments, outside any conversation, so it can be tried from the editor.</summary>
    [HttpPost("custom/test")]
    public async Task<IActionResult> TestCustom([FromBody] TestBody? body, CancellationToken ct)
        => string.IsNullOrWhiteSpace(body?.Name)
            ? BadRequest(new { error = "Say which tool to try." })
            : Ok(new { result = await CustomTools.TestCall(body.Name, string.IsNullOrWhiteSpace(body.Args) ? "{}" : body.Args, ct) });
}

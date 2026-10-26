using ARI.API.Auth;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>The global run_command allowlist (see <see cref="CommandAllowlistStore"/>). Admin only — commands run
/// on the admin's own machine, so only the admin decides what runs unattended.</summary>
[Route("command-allowlist")]
[ApiController]
public class CommandAllowlistController : ControllerBase
{
    public sealed record AllowlistBody(List<string>? Commands);

    [HttpGet]
    public IActionResult Get()
        => User.IsInRole(Roles.Admin) ? Ok(new { commands = CommandAllowlistStore.Get() }) : Forbid();

    [HttpPut]
    public IActionResult Put([FromBody] AllowlistBody? body)
        => User.IsInRole(Roles.Admin) ? Ok(new { commands = CommandAllowlistStore.Set(body?.Commands) }) : Forbid();
}

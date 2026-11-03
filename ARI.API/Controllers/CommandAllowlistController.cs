using ARI.API.Auth;
using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>The global command allowlist (see <see cref="CommandAllowlistStore"/>), shared by the shell tool and the desktop
/// app's run_command. Admin only — commands run on the admin's own machine, so only the admin decides what runs unattended.</summary>
[Route("api/v1/admin/commandAllowlist")]
[ApiController]
public class CommandAllowlistController : ControllerBase
{
    public sealed record AllowlistBody(List<string>? Commands);
    public sealed record CheckBody(string? Command);

    [HttpGet]
    public IActionResult Get()
        => Ok(new { commands = CommandAllowlistStore.Get() });

    /// <summary>Replaces the list. Keys that must never be stored (a bare "git", "sudo") are dropped.</summary>
    [HttpPut]
    public IActionResult Put([FromBody] AllowlistBody? body)
        => Ok(new { commands = CommandAllowlistStore.Set((body?.Commands ?? new()).Select(c => (c ?? "").Trim()).Where(CommandPolicy.StorableKey)) });

    /// <summary>The commands in a command line that still need the owner's decision (read-only and allowlisted ones are
    /// left out), one per distinct command. The desktop app asks this before running a run_command, so both paths use the
    /// same rules (see <see cref="CommandPolicy"/>).</summary>
    [HttpPost("check")]
    public IActionResult Check([FromBody] CheckBody? body)
    {
        string command = body?.Command ?? "";
        List<CommandSegment> needing = CommandPolicy.NeedingDecision(command, CommandAllowlistStore.Get());
        return Ok(new { segments = needing.Select(s => new { text = s.Text, key = s.Key, tier = s.Tier.ToString().ToLowerInvariant(), reason = s.Reason }) });
    }
}

using ARI.API.Data;
using ARI.BrainVault;
using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>
/// Control-panel endpoints for managing brain backups: list, create, and restore.
/// Restore is additive (recreates missing notes, overwrites present ones to the snapshot) and
/// never deletes, so it is safe to run against a damaged graph.
/// </summary>
[Route("admin/brain")]
[ApiController]
public class BrainController(PersistentData persistentData) : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    /// <summary>Lists available backups, newest first.</summary>
    [HttpGet("backups")]
    public IActionResult Backups()
    {
        if (Llm is null || !Llm.BrainAvailable) return Ok(Array.Empty<object>());
        var backups = Llm.ListBrainBackups()
            .Select(b => new
            {
                file      = b.FileName,
                created   = b.Created,
                sizeBytes = b.SizeBytes,
                noteCount = b.NoteCount
            });
        return Ok(backups);
    }

    /// <summary>Creates a new backup of the current brain.</summary>
    [HttpPost("backup")]
    public IActionResult CreateBackup()
    {
        if (Llm is null || !Llm.BrainAvailable) return BadRequest(new { message = "Brain is not available." });
        return Ok(new { message = Llm.BackupBrain() });
    }

    public record RestoreRequest(string File);

    /// <summary>Restores notes from the named backup (additive, never deletes).</summary>
    [HttpPost("restore")]
    public IActionResult Restore([FromBody] RestoreRequest request)
    {
        if (Llm is null || !Llm.BrainAvailable) return BadRequest(new { message = "Brain is not available." });
        if (string.IsNullOrWhiteSpace(request.File)) return BadRequest(new { message = "No backup file specified." });
        return Ok(new { message = Llm.RestoreBrainBackup(request.File) });
    }

    /// <summary>How many completed exchanges an active thread accumulates before Engram sweeps it
    /// mid-conversation, instead of waiting for the thread to go dormant or be closed. 0 = disabled.</summary>
    [HttpGet("engram-interval")]
    public IActionResult GetEngramInterval()
    {
        int turns = Llm?.GetEngramTurnInterval() ?? persistentData.GetAgent("Engram")?.TurnsBeforeSweep ?? 5;
        return Ok(new { turns });
    }

    public record EngramIntervalRequest(int Turns);

    /// <summary>Persists to Agents.json AND applies live to the running Engram instance — no restart needed.</summary>
    [HttpPut("engram-interval")]
    public IActionResult SetEngramInterval([FromBody] EngramIntervalRequest request)
    {
        int turns = Math.Max(0, request.Turns);

        AgentDefinition? agent = persistentData.GetAgent("Engram");
        if (agent is null) return NotFound(new { message = "Engram agent definition not found." });
        agent.TurnsBeforeSweep = turns;
        persistentData.UpdateAgent(agent);

        Llm?.SetEngramTurnInterval(turns);
        return Ok(new { turns });
    }
}

using ARI.Brain;
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
public class BrainController : ControllerBase
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
    public async Task<IActionResult> CreateBackup()
    {
        if (Llm is null || !Llm.BrainAvailable) return BadRequest(new { message = "Brain is not available." });
        string result = await Llm.BackupBrain();
        return Ok(new { message = result });
    }

    public record RestoreRequest(string File);

    /// <summary>Restores notes from the named backup (additive, never deletes).</summary>
    [HttpPost("restore")]
    public async Task<IActionResult> Restore([FromBody] RestoreRequest request)
    {
        if (Llm is null || !Llm.BrainAvailable) return BadRequest(new { message = "Brain is not available." });
        if (string.IsNullOrWhiteSpace(request.File)) return BadRequest(new { message = "No backup file specified." });
        string result = await Llm.RestoreBrainBackup(request.File);
        return Ok(new { message = result });
    }
}

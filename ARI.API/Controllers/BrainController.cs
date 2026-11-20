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
[Route("api/v1/admin/brain")]
[ApiController]
public class BrainController : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    /// <summary>A read-only look at how tidy the graph is: dead ends, orphans, probable duplicates, untyped and stale notes.
    /// Titles and counts only. Nothing is changed; the owner decides what to fix.</summary>
    [HttpGet("health")]
    public IActionResult Health()
    {
        if (Llm is null || !Llm.BrainAvailable) return BadRequest(new { error = "Brain is not available." });
        BrainHealthReport r = BrainHealth.Scan();
        return Ok(new
        {
            notes = r.Notes, unresolvedLinks = r.UnresolvedLinks,
            deadEnds = r.DeadEnds, orphans = r.Orphans, untyped = r.Untyped, stale = r.Stale,
            duplicates = r.DuplicateCandidates.Select(d => new { a = d.A, b = d.B }),
        });
    }

    /// <summary>Every note's name (folder/title), for browsing the brain by folder. Read-only.</summary>
    [HttpGet("notes")]
    public IActionResult Notes()
    {
        if (Llm is null || !Llm.BrainAvailable) return BadRequest(new { error = "Brain is not available." });
        Brain.IndexIfChanged();
        return Ok(Brain.GetPaths().Select(p => p.EndsWith(".md") ? p[..^3] : p).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>One note to read: its title, type, whether it's private, and its text. Read-only.</summary>
    [HttpGet("note")]
    public IActionResult Note([FromQuery] string path)
    {
        if (Llm is null || !Llm.BrainAvailable) return BadRequest(new { error = "Brain is not available." });
        if (Brain.GetNote(path ?? "") is not { } n) return NotFound(new { error = "No such note." });
        return Ok(new { name = n.Name, title = n.Title, type = n.Type, sensitive = n.IsSensitive, aliases = n.Aliases, keywords = n.Keywords, content = n.Content });
    }

    /// <summary>The curator's pending suggestions for the brain (merge, type, link).</summary>
    [HttpGet("proposals")]
    public IActionResult Proposals() => Ok(BrainCuration.List().Where(p => p.Status == "pending").OrderByDescending(p => p.Created));

    public sealed record ProposalDecision(string Id, bool Approve);

    /// <summary>Approve (make the change) or dismiss one suggestion.</summary>
    [HttpPost("proposals")]
    public IActionResult Decide([FromBody] ProposalDecision d)
    {
        if (Llm is null || !Llm.BrainAvailable) return BadRequest(new { error = "Brain is not available." });
        BrainProposal? p = BrainCuration.Decide(d.Id, d.Approve);
        return p is null ? NotFound() : Ok(new { status = p.Status, result = p.Result });
    }

    /// <summary>Asks the curator for suggestions now, instead of waiting for an idle moment.</summary>
    [HttpPost("proposals/scan")]
    public async Task<IActionResult> ScanNow()
    {
        if (Llm is null || !Llm.BrainAvailable || Reflector.Current is null) return BadRequest(new { error = "Brain or reflector is not available." });
        int n = await BrainCuration.Suggest(Reflector.Current, 3, HttpContext.RequestAborted);
        return Ok(new { added = n });
    }

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
        return Ok(this.Page(backups.ToList()));
    }

    /// <summary>Creates a new backup of the current brain.</summary>
    [HttpPost("backups")]
    public IActionResult CreateBackup()
    {
        if (Llm is null || !Llm.BrainAvailable) return BadRequest(new { error = "Brain is not available." });
        return Ok(new { message = Llm.BackupBrain() });
    }

    public record RestoreRequest(string File);

    /// <summary>Restores notes from the named backup (additive, never deletes).</summary>
    [HttpPost("restore")]
    public IActionResult Restore([FromBody] RestoreRequest request)
    {
        if (Llm is null || !Llm.BrainAvailable) return BadRequest(new { error = "Brain is not available." });
        if (string.IsNullOrWhiteSpace(request.File)) return BadRequest(new { error = "No backup file specified." });
        if (!SafeNames.IsSafe(request.File)) return BadRequest(new { error = "That name is not allowed." });
        return Ok(new { message = Llm.RestoreBrainBackup(request.File) });
    }
}

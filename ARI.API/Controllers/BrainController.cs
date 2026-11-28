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

    /// <summary>The whole brain as a graph for the Brain page: every note (name, title, top folder, type, length) and every link
    /// between two notes. Titles and links only; a note's text is fetched one at a time from <see cref="Note"/>. Read-only.</summary>
    [HttpGet("graph")]
    public IActionResult Graph()
    {
        if (Llm is null || !Llm.BrainAvailable) return BadRequest(new { error = "Brain is not available." });
        Brain.IndexIfChanged();
        List<Note> notes = Brain.GetTitles().Select(t => Brain.GetNote(t)).OfType<Note>().ToList();
        Dictionary<string, int> id = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < notes.Count; i++) id.TryAdd(notes[i].Name, i);
        Dictionary<string, int> byTitle = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < notes.Count; i++) byTitle.TryAdd(notes[i].Title, i);
        HashSet<(int, int)> links = new();
        for (int i = 0; i < notes.Count; i++)
            foreach (Note to in notes[i].GetLinks())
                if (id.TryGetValue(to.Name, out int j) && j != i) links.Add((Math.Min(i, j), Math.Max(i, j)));
        return Ok(new
        {
            nodes = notes.Select((n, i) => new
            {
                id = i, name = n.Name, title = n.Title, type = n.Type, real = true,
                folder = n.Folder.Length == 0 ? "Core" : n.Folder.Split('/')[0],
                size = SizeOf(n),
                group = IsGroup(n),   // a group's note anchors its neighbourhood in the view, however many links it has
                groups = GroupsOf(n).Select(g => id.TryGetValue(g, out int j) ? j : byTitle.TryGetValue(g, out int k) ? k : -1).Where(j => j >= 0 && j != i).Distinct(),
            }),
            links = links.Select(l => new[] { l.Item1, l.Item2 }),
        });

        static int SizeOf(Note n) { try { return n.Content.Length; } catch { return 0; } }
        static IEnumerable<string> GroupsOf(Note n)
        {
            string body; try { body = n.Content; } catch { return []; }
            System.Text.RegularExpressions.Match line = System.Text.RegularExpressions.Regex.Match(body, @"(?m)^\*\*Groups:\*\*(.*)$");
            return line.Success ? System.Text.RegularExpressions.Regex.Matches(line.Groups[1].Value, @"\[\[([^\]|#]+)").Select(m => m.Groups[1].Value.Trim()).ToList() : [];
        }
        static bool IsGroup(Note n) { try { return n.Type == "group" || n.Content.Contains("**Members:**"); } catch { return false; } }
    }

    /// <summary>One note to read: its title, type, whether it's private, and its text. Read-only.</summary>
    [HttpGet("note")]
    public IActionResult Note([FromQuery] string path)
    {
        if (Llm is null || !Llm.BrainAvailable) return BadRequest(new { error = "Brain is not available." });
        if (Brain.GetNote(path ?? "") is not { } n) return NotFound(new { error = "No such note." });
        return Ok(new { name = n.Name, title = n.Title, type = n.Type, sensitive = n.IsSensitive, aliases = n.Aliases, keywords = n.Keywords, content = n.Content });
    }

    public sealed record ReworkRequest(List<string>? Scope, string? Instructions);

    /// <summary>Asks Engram to reshape the brain: split notes that hold many subjects into small linked ones, give shared names notes of
    /// their own and group notes into theme hubs. It runs while ARI is idle and commits every step, so it can be undone with git.</summary>
    [HttpPost("rework")]
    public IActionResult Rework([FromBody] ReworkRequest? r)
    {
        if (Llm is null || !Llm.BrainAvailable) return BadRequest(new { error = "Brain is not available." });
        return Ok(new { message = Llm.QueueBrainRework(r?.Scope, r?.Instructions), queued = Llm.QueuedBrainReworks });
    }

    [HttpGet("rework")]
    public IActionResult ReworkStatus() => Ok(new { queued = Llm?.QueuedBrainReworks ?? 0 });

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

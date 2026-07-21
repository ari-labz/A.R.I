using System.Text.Json.Serialization;
using ARI.API;
using ARI.Brain;
using ARI.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ARI.API.Controllers;

[Route("projects")]
[ApiController]
public class ProjectsController(ProjectStore store) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll() => Ok(store.GetAll());

    [HttpPost]
    public IActionResult Create([FromBody] CreateProjectRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name is required." });

        string id = Guid.NewGuid().ToString("N");
        ProjectType type = req.Type ?? ProjectType.Repository;
        // Default per type (a repo is usually worked on locally via the desktop app; a note graph is
        // small enough to live centrally) — overridable by an explicit request.
        StorageBackend backend = req.Backend ?? (type == ProjectType.ObsidianGraph ? StorageBackend.ServerFs : StorageBackend.RemoteFs);
        string name = req.Name.Trim();
        // ServerFs root is server-managed — derived + created here, never user-typed. RemoteFs keeps
        // RootPath null server-side; the desktop app's own per-device local-path store covers that case.
        string? rootPath = backend == StorageBackend.ServerFs ? ProjectStore.CreateServerFolder(id, name) : null;

        Project project = new(
            Id:           id,
            Name:         name,
            Description:  req.Description?.Trim() ?? "",
            Instructions: req.Instructions?.Trim() ?? "",
            CreatedAt:    DateTime.UtcNow,
            Type:         type,
            Category:     req.Category?.Trim() ?? "",
            Backend:      backend,
            RootPath:     rootPath);

        store.Add(project);
        EnsureBrainNote(project);
        return Ok(project);
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] CreateProjectRequest req)
    {
        Project? existing = store.Get(id);
        if (existing is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name is required." });

        // Type/Category are editable after creation; Backend/RootPath are not — changing where a
        // project's files actually live is a bigger operation (a move, not a field edit) and isn't
        // wired up yet.
        Project updated = existing with
        {
            Name         = req.Name.Trim(),
            Description  = req.Description?.Trim() ?? "",
            Instructions = req.Instructions?.Trim() ?? "",
            Type         = req.Type ?? existing.Type,
            Category     = req.Category?.Trim() ?? existing.Category,
        };
        store.Update(updated);
        if (updated.Name != existing.Name) RenameBrainNote(existing.Name, updated.Name);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        if (store.Get(id) is null) return NotFound();
        // The brain note is deliberately left alone — a project you delete may still be worth
        // remembering happened. Nothing here ever deletes or archives it.
        store.Delete(id);
        return Ok();
    }

    // ── Brain note (Projects/[Name]) ────────────────────────────────────────────────
    // Deterministic, structural fields only (type/category/backend) — the descriptive summary is
    // Engram's job over time, same tending discipline as any other note, not something this
    // controller ever overwrites.

    private static void EnsureBrainNote(Project project)
    {
        if (!BrainModule.Ready) return;
        try
        {
            List<EngramAdd> adds = new();
            if (BrainModule.GetNote("Projects") is null)
                adds.Add(new EngramAdd { NoteName = "Projects", Content = "Hub for every project Ari knows about.", Type = "hub" });
            adds.Add(new EngramAdd
            {
                NoteName = $"Projects/{project.Name}",
                Content  = ProjectNoteBody(project),
                Type     = "project",
            });
            BrainModule.AddNotes(adds);
            // The hub links DOWN to each direct child (GraphRulebook) — deterministic, idempotent,
            // safe to call even if other hubs in the vault also happen to be missing a member link.
            BrainModule.EnsureHubChildLinks();
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning(ex, "[Projects] Failed to create brain note for project '{Name}'.", project.Name);
        }
    }

    private static void RenameBrainNote(string oldName, string newName)
    {
        if (!BrainModule.Ready) return;
        try
        {
            Note? existing = BrainModule.GetNote(oldName);
            if (existing is null) return; // no note to rename (e.g. brain wasn't ready at creation)
            BrainModule.EditNotes(new[]
            {
                new EngramEdit
                {
                    NoteName    = oldName,
                    NewNoteName = $"Projects/{newName}",
                    Content     = existing.Content,
                    Aliases     = existing.Aliases,
                    Type        = "project",
                }
            });
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning(ex, "[Projects] Failed to rename brain note '{Old}' -> '{New}'.", oldName, newName);
        }
    }

    private static string ProjectNoteBody(Project project) =>
        $"Type: {project.Type}\n" +
        $"Category: {(project.Category.Length > 0 ? project.Category : "none")}\n" +
        $"Storage: {project.Backend}\n\n" +
        "(No summary yet — Ari will fill this in as we discuss the project.)\n\n" +
        "[[Projects]]\n";

    // ── Project attachments ───────────────────────────────────────────────────────

    [HttpGet("{id}/attachments")]
    public IActionResult GetAttachments(string id)
    {
        if (store.Get(id) is null) return NotFound();
        return Ok(store.GetAttachmentNames(id).Select(n => new { name = n }));
    }

    [HttpPost("{id}/attachments")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> AddAttachment(string id, IFormFile file)
    {
        if (store.Get(id) is null) return NotFound();
        if (file is null || file.Length == 0) return BadRequest("No file provided.");

        using MemoryStream ms = new();
        await file.CopyToAsync(ms);
        store.SaveAttachment(id, file.FileName, ms.ToArray());
        return Ok(new { name = file.FileName });
    }

    [HttpDelete("{id}/attachments/{name}")]
    public IActionResult DeleteAttachment(string id, string name)
    {
        if (store.Get(id) is null) return NotFound();
        store.DeleteAttachment(id, name);
        return Ok();
    }
}

public record CreateProjectRequest(
    string Name, string? Description, string? Instructions,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProjectType? Type,
    string? Category,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] StorageBackend? Backend);

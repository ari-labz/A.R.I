using System.Text.Json.Serialization;
using ARI.API;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

[Route("projects")]
[ApiController]
public class ProjectsController(ProjectStore store, ProjectServiceAdapter projects) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll() => Ok(store.GetAll());

    [HttpPost]
    public IActionResult Create([FromBody] CreateProjectRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name is required." });

        // The adapter only takes name/type/category (the shape a tool call also needs) — Description/
        // Instructions/explicit Backend aren't part of that shared contract, so they're applied here,
        // after creation, REST-only. The adapter always uses the type's default Backend; this patches
        // it if the request explicitly asked for something else.
        var summary = projects.Create(req.Name, (req.Type ?? ProjectType.Repository).ToString(), req.Category);
        if (summary is null) return BadRequest(new { error = "Failed to create project." });

        Project? created = store.Get(summary.Id);
        if (created is null) return StatusCode(500);

        created = created with
        {
            Description  = req.Description?.Trim() ?? created.Description,
            Instructions = req.Instructions?.Trim() ?? created.Instructions,
        };
        if (req.Backend is { } explicitBackend && explicitBackend != created.Backend)
            created = created with
            {
                Backend  = explicitBackend,
                // RootPath only ever means something for ServerFs — clear it going the other way,
                // create it (if not already there) going this way.
                RootPath = explicitBackend == StorageBackend.ServerFs
                    ? created.RootPath ?? ProjectStore.CreateServerFolder(created.Id, created.Name)
                    : null,
            };
        store.Update(created);

        return Ok(created);
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] CreateProjectRequest req)
    {
        Project? existing = store.Get(id);
        if (existing is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name is required." });

        // Rename (if any) goes through the adapter so the brain note stays in sync; other fields are
        // plain REST-only updates.
        if (req.Name.Trim() != existing.Name) projects.Rename(id, req.Name.Trim());

        // Type/Category are editable after creation; Backend/RootPath are not — changing where a
        // project's files actually live is a bigger operation (a move, not a field edit) and isn't
        // wired up yet.
        Project updated = (store.Get(id) ?? existing) with
        {
            Description  = req.Description?.Trim() ?? "",
            Instructions = req.Instructions?.Trim() ?? "",
            Type         = req.Type ?? existing.Type,
            Category     = req.Category?.Trim() ?? existing.Category,
        };
        store.Update(updated);
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

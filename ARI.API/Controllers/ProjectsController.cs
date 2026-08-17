using System.Security.Claims;
using System.Text.Json.Serialization;
using ARI.API;
using ARI.API.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

[Route("projects")]
[ApiController]
public class ProjectsController(ProjectStore store, ProjectServiceAdapter projects, UserStore users) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll()
    {
        if (IsAdmin())
        {
            // Admins see everything — join owner username so the control panel can display it.
            var allUsers = users.GetAll().ToDictionary(u => u.Id, u => u.Username);
            return Ok(store.GetAll().Select(p => new
            {
                p.Id, p.Name, p.Description, p.Instructions, p.CreatedAt,
                p.Category, p.Backend, p.RootPath, p.Attachments, p.OwnerId,
                OwnerUsername = p.OwnerId == 0 ? "admin" : (allUsers.TryGetValue(p.OwnerId, out string? n) ? n : "unknown"),
            }));
        }

        // Guests see only their own projects.
        int callerId = CallerId();
        return Ok(store.GetByOwner(callerId));
    }

    [HttpPost]
    public IActionResult Create([FromBody] CreateProjectRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name is required." });

        var summary = projects.Create(req.Name, req.Category, req.Backend?.ToString());
        if (summary is null) return BadRequest(new { error = "Failed to create project." });

        Project? created = store.Get(summary.Id);
        if (created is null) return StatusCode(500);

        int ownerId = IsAdmin() ? 0 : CallerId();
        created = created with
        {
            Description  = req.Description?.Trim() ?? created.Description,
            Instructions = req.Instructions?.Trim() ?? created.Instructions,
            OwnerId      = ownerId,
        };
        store.Update(created);

        return Ok(created);
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] CreateProjectRequest req)
    {
        Project? existing = store.Get(id);
        if (existing is null) return NotFound();
        if (!CanAccess(existing)) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name is required." });

        if (req.Name.Trim() != existing.Name) projects.Rename(id, req.Name.Trim());

        Project current = store.Get(id) ?? existing;
        StorageBackend newBackend = req.Backend ?? current.Backend;
        string? newRootPath = current.RootPath;
        if (newBackend != current.Backend)
        {
            newRootPath = newBackend == StorageBackend.ServerFs
                ? ProjectStore.CreateServerFolder(id, req.Name.Trim())
                : null;
        }

        Project updated = current with
        {
            Description  = req.Description?.Trim() ?? "",
            Instructions = req.Instructions?.Trim() ?? "",
            Category     = req.Category?.Trim() ?? current.Category,
            Backend      = newBackend,
            RootPath     = newRootPath,
        };
        store.Update(updated);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        Project? project = store.Get(id);
        if (project is null) return NotFound();
        if (!CanAccess(project)) return Forbid();
        store.Delete(id);
        return Ok();
    }

    // ── Project attachments ───────────────────────────────────────────────────────

    [HttpGet("{id}/attachments")]
    public IActionResult GetAttachments(string id)
    {
        Project? project = store.Get(id);
        if (project is null) return NotFound();
        if (!CanAccess(project)) return Forbid();
        return Ok(store.GetAttachmentNames(id).Select(n => new { name = n }));
    }

    [HttpPost("{id}/attachments")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> AddAttachment(string id, IFormFile file)
    {
        Project? project = store.Get(id);
        if (project is null) return NotFound();
        if (!CanAccess(project)) return Forbid();
        if (file is null || file.Length == 0) return BadRequest("No file provided.");

        using MemoryStream ms = new();
        await file.CopyToAsync(ms);
        store.SaveAttachment(id, file.FileName, ms.ToArray());
        return Ok(new { name = file.FileName });
    }

    [HttpDelete("{id}/attachments/{name}")]
    public IActionResult DeleteAttachment(string id, string name)
    {
        Project? project = store.Get(id);
        if (project is null) return NotFound();
        if (!CanAccess(project)) return Forbid();
        store.DeleteAttachment(id, name);
        return Ok();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private bool IsAdmin() => User.FindFirstValue(ClaimTypes.Role) == Roles.Admin;

    private int CallerId()
    {
        string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(sub, out int id) ? id : 0;
    }

    // Admins can access any project; guests can only access their own.
    private bool CanAccess(Project p) => IsAdmin() || p.OwnerId == CallerId();
}

public record CreateProjectRequest(
    string Name, string? Description, string? Instructions,
    string? Category,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] StorageBackend? Backend);

using System.Security.Claims;
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
            Dictionary<int, string> allUsers = users.GetAll().ToDictionary(u => u.Id, u => u.Username);
            return Ok(store.GetAll().Select(p => new
            {
                p.Id, p.Name, p.Description, p.Instructions, p.CreatedAt,
                p.Category, p.Backend, p.RootPath, p.OwnerId,
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

        ARI.Common.ProjectSummary? summary = projects.Create(req.Name, req.Category, "ServerFs");
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

        // Ensure every project has a server folder — migrate legacy RemoteFs projects on first save.
        string rootPath = current.RootPath ?? ProjectStore.CreateServerFolder(id, req.Name.Trim());

        Project updated = current with
        {
            Description  = req.Description?.Trim() ?? "",
            Instructions = req.Instructions?.Trim() ?? "",
            Category     = req.Category?.Trim() ?? current.Category,
            Backend      = StorageBackend.ServerFs,
            RootPath     = rootPath,
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
    string? Category);

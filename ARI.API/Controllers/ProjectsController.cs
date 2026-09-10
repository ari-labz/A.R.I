using System.Security.Claims;
using ARI.API;
using ARI.API.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

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

    // ── Files (ServerFs projects only) ──────────────────────────────────────────

    private static readonly HashSet<string> HiddenEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ariproject", ".ariignore", ".git",
    };

    [HttpGet("{id}/files")]
    public IActionResult ListFiles(string id, [FromQuery] string path = "")
    {
        (Project? project, string? dir, IActionResult? error) = ResolveDir(id, path);
        if (error is not null) return error;

        var entries = Directory.EnumerateFileSystemEntries(dir!)
            .Where(p => !HiddenEntries.Contains(Path.GetFileName(p)))
            .Select(p =>
            {
                bool isDir = Directory.Exists(p);
                FileSystemInfo info = isDir ? new DirectoryInfo(p) : new FileInfo(p);
                return new
                {
                    name = info.Name,
                    isDir,
                    size = isDir ? (long?)null : ((FileInfo)info).Length,
                    modifiedAt = info.LastWriteTimeUtc,
                };
            })
            .OrderByDescending(e => e.isDir).ThenBy(e => e.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new { path, entries });
    }

    [HttpGet("{id}/files/content")]
    public IActionResult DownloadFile(string id, [FromQuery] string path)
    {
        Project? project = store.Get(id);
        if (project is null) return NotFound();
        if (!CanAccess(project)) return Forbid();
        if (project.Backend != StorageBackend.ServerFs || project.RootPath is null)
            return BadRequest(new { error = "This project has no server filesystem." });

        string? abs = ResolvePath(project.RootPath, path);
        if (abs is null) return BadRequest(new { error = "Invalid path." });
        if (!System.IO.File.Exists(abs)) return NotFound();

        string contentType = "application/octet-stream";
        new FileExtensionContentTypeProvider().TryGetContentType(abs, out string? ct);
        return PhysicalFile(abs, ct ?? contentType, Path.GetFileName(abs));
    }

    [HttpPost("{id}/files")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadFiles(string id, [FromQuery] string path = "")
    {
        (Project? project, string? dir, IActionResult? error) = ResolveDir(id, path);
        if (error is not null) return error;

        if (Request.Form.Files.Count == 0)
            return BadRequest(new { error = "No files in request." });

        List<string> saved = new();
        foreach (IFormFile file in Request.Form.Files)
        {
            string safeName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(safeName)) continue;
            string dest = Path.Combine(dir!, safeName);
            await using FileStream fs = System.IO.File.Create(dest);
            await file.CopyToAsync(fs);
            saved.Add(safeName);
        }
        return Ok(new { saved });
    }

    [HttpDelete("{id}/files")]
    public IActionResult DeleteFile(string id, [FromQuery] string path)
    {
        Project? project = store.Get(id);
        if (project is null) return NotFound();
        if (!CanAccess(project)) return Forbid();
        if (project.Backend != StorageBackend.ServerFs || project.RootPath is null)
            return BadRequest(new { error = "This project has no server filesystem." });
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "Refusing to delete the project root." });

        string? abs = ResolvePath(project.RootPath, path);
        if (abs is null) return BadRequest(new { error = "Invalid path." });

        if (Directory.Exists(abs)) Directory.Delete(abs, recursive: true);
        else if (System.IO.File.Exists(abs)) System.IO.File.Delete(abs);
        else return NotFound();

        return Ok();
    }

    /// <summary>Resolves and validates a project + subdirectory for the file-browsing endpoints.</summary>
    private (Project?, string?, IActionResult?) ResolveDir(string id, string path)
    {
        Project? project = store.Get(id);
        if (project is null) return (null, null, NotFound());
        if (!CanAccess(project)) return (null, null, Forbid());
        if (project.Backend != StorageBackend.ServerFs || project.RootPath is null)
            return (null, null, BadRequest(new { error = "This project has no server filesystem." }));

        string? abs = ResolvePath(project.RootPath, path);
        if (abs is null) return (null, null, BadRequest(new { error = "Invalid path." }));
        if (!Directory.Exists(abs)) return (null, null, NotFound(new { error = "Directory not found." }));

        return (project, abs, null);
    }

    /// <summary>Resolves a project-relative path to an absolute one, refusing traversal outside root.</summary>
    private static string? ResolvePath(string root, string relPath)
    {
        string abs = Path.GetFullPath(Path.Combine(root, relPath ?? ""));
        string rootFull = Path.GetFullPath(root);
        return abs == rootFull || abs.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? abs : null;
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

using System.Security.Claims;
using ARI.API;
using ARI.API.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace ARI.API.Controllers;

[Route("api/v1/projects")]
[ApiController]
public class ProjectsController(ProjectStore store, ProjectServiceAdapter projects, UserStore users) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll()
    {
        // Admins see everything, with the owner's name beside each; everyone else sees only their own.
        Dictionary<int, string> names = IsAdmin() ? users.GetAll().ToDictionary(u => u.Id, u => u.Username) : [];
        IEnumerable<Project> mine = IsAdmin() ? store.GetAll() : store.GetByOwner(CallerId());
        return Ok(this.Page(mine.Select(p => Shape(p, names)).ToList()));
    }

    /// <summary>The one shape a project is sent in. Where it lives on the server's disk, and whose it is, are for admins only.</summary>
    private object Shape(Project p, Dictionary<int, string>? names = null)
    {
        // A project's backend is sent as its name ("ServerFs"), not the number it is stored as.
        if (!IsAdmin())
            return new { p.Id, p.Name, p.Description, p.Instructions, p.CreatedAt, p.Category, Backend = p.Backend.ToString() };
        string owner = p.OwnerId == 0 ? "admin"
            : names is not null && names.TryGetValue(p.OwnerId, out string? n) ? n
            : users.GetById(p.OwnerId)?.Username ?? "unknown";
        return new { p.Id, p.Name, p.Description, p.Instructions, p.CreatedAt, p.Category, Backend = p.Backend.ToString(), p.RootPath, p.OwnerId, OwnerUsername = owner };
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

        return Ok(Shape(created));
    }

    /// <summary>Change a project. Send only what changes.</summary>
    [HttpPatch("{id}")]
    public IActionResult Update(string id, [FromBody] CreateProjectRequest req)
    {
        Project? existing = store.Get(id);
        if (existing is null) return NotFound();
        if (!CanAccess(existing)) return StatusCode(403);
        if (req.Name is not null && string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name can't be empty." });

        string name = req.Name?.Trim() ?? existing.Name;
        if (name != existing.Name) projects.Rename(id, name);

        Project current = store.Get(id) ?? existing;

        // Ensure every project has a server folder — migrate legacy RemoteFs projects on first save.
        string rootPath = current.RootPath ?? ProjectStore.CreateServerFolder(id, name);

        Project updated = current with
        {
            Description  = req.Description?.Trim() ?? current.Description,
            Instructions = req.Instructions?.Trim() ?? current.Instructions,
            Category     = req.Category?.Trim() ?? current.Category,
            Backend      = StorageBackend.ServerFs,
            RootPath     = rootPath,
        };
        store.Update(updated);
        return Ok(Shape(updated));
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        Project? project = store.Get(id);
        if (project is null) return NotFound();
        if (!CanAccess(project)) return StatusCode(403);
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
        if (!CanAccess(project)) return StatusCode(403);
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
    [RequestSizeLimit(100L * 1024 * 1024)]
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
        if (!CanAccess(project)) return StatusCode(403);
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
        if (!CanAccess(project)) return (null, null, StatusCode(403));
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
        if (abs != rootFull && !abs.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        // ARI's own git bookkeeping is off limits, and so is anything that is really a link pointing outside the project.
        if (ProjectStore.IsProtectedPath(Path.GetRelativePath(rootFull, abs))) return null;
        try
        {
            FileSystemInfo? real = (Directory.Exists(abs) ? new DirectoryInfo(abs) : (FileSystemInfo)new FileInfo(abs)).ResolveLinkTarget(true);
            if (real is not null && !Path.GetFullPath(real.FullName).StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch { return null; }
        return abs;
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

using System.Security.Claims;
using ARI.API.Auth;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>
/// ARI Project Sync — lets ARI Desktop push and pull the hidden .ariproject git repo inside a
/// project's server folder. Uses git bundles over HTTP rather than the full git smart HTTP protocol,
/// so no git http-backend CGI plumbing is required.
///
/// Transport:
///   GET  /projects/{id}/sync/status  — returns {serverSha, clientSha (echo), ahead, behind}
///   GET  /projects/{id}/sync/pull    — streams a git bundle of changes since the client's SHA
///   POST /projects/{id}/sync/push    — accepts a git bundle, applies it, commits to server
/// </summary>
[Route("projects/{id}/sync")]
[ApiController]
public class ProjectSyncController(ProjectStore store) : ControllerBase
{
    // ── Status ────────────────────────────────────────────────────────────────────

    [HttpGet("status")]
    public IActionResult GetStatus(string id, [FromQuery] string? clientSha)
    {
        Project? project = store.Get(id);
        if (project is null) return NotFound();
        if (!CanAccess(project)) return Forbid();
        if (project.RootPath is not { } root) return BadRequest(new { error = "Project has no server folder." });

        string ariDir = Path.Combine(root, ".ariproject");
        if (!Directory.Exists(ariDir))
            return BadRequest(new { error = "Project sync not initialised." });

        var (_, serverSha) = ProjectStore.RunGit(root, "rev-parse HEAD");
        serverSha = serverSha.Trim();

        int ahead = 0, behind = 0;
        if (!string.IsNullOrWhiteSpace(clientSha) && clientSha != serverSha)
        {
            var (_, aheadStr) = ProjectStore.RunGit(root,
                $"rev-list --count {serverSha}..{clientSha}");
            var (_, behindStr) = ProjectStore.RunGit(root,
                $"rev-list --count {clientSha}..{serverSha}");
            int.TryParse(aheadStr.Trim(), out ahead);
            int.TryParse(behindStr.Trim(), out behind);
        }

        return Ok(new { serverSha, clientSha, ahead, behind });
    }

    // ── Pull (server → client) ────────────────────────────────────────────────────

    /// <summary>Returns a git bundle containing all commits the client is missing.
    /// If clientSha is omitted, bundles everything (for first-time clone).</summary>
    [HttpGet("pull")]
    public IActionResult Pull(string id, [FromQuery] string? clientSha)
    {
        Project? project = store.Get(id);
        if (project is null) return NotFound();
        if (!CanAccess(project)) return Forbid();
        if (project.RootPath is not { } root) return BadRequest(new { error = "Project has no server folder." });

        string tmp = Path.GetTempFileName();
        try
        {
            string bundleArgs = string.IsNullOrWhiteSpace(clientSha)
                ? $"bundle create \"{tmp}\" --all"
                : $"bundle create \"{tmp}\" {clientSha}..HEAD";

            var (code, output) = ProjectStore.RunGit(root, bundleArgs);
            if (code != 0)
                return BadRequest(new { error = $"Bundle failed: {output}" });

            byte[] bytes = System.IO.File.ReadAllBytes(tmp);
            return File(bytes, "application/octet-stream", "ari-project.bundle");
        }
        finally
        {
            if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        }
    }

    // ── Push (client → server) ────────────────────────────────────────────────────

    /// <summary>Accepts a git bundle from the Desktop and applies it to the server's .ariproject.</summary>
    [HttpPost("push")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> Push(string id)
    {
        Project? project = store.Get(id);
        if (project is null) return NotFound();
        if (!CanAccess(project)) return Forbid();
        if (project.RootPath is not { } root) return BadRequest(new { error = "Project has no server folder." });

        string tmp = Path.GetTempFileName();
        try
        {
            using (var fs = System.IO.File.OpenWrite(tmp))
                await Request.Body.CopyToAsync(fs);

            // Verify the bundle is valid before applying.
            var (verifyCode, verifyOut) = ProjectStore.RunGit(root, $"bundle verify \"{tmp}\"");
            if (verifyCode != 0)
                return BadRequest(new { error = $"Invalid bundle: {verifyOut}" });

            // Fetch refs from the bundle into the .ariproject repo.
            var (fetchCode, fetchOut) = ProjectStore.RunGit(root, $"bundle unbundle \"{tmp}\"");
            if (fetchCode != 0)
                return BadRequest(new { error = $"Unbundle failed: {fetchOut}" });

            // Fast-forward HEAD to the incoming tip.
            ProjectStore.RunGit(root, "merge FETCH_HEAD --ff-only");

            var (_, newSha) = ProjectStore.RunGit(root, "rev-parse HEAD");
            return Ok(new { serverSha = newSha.Trim() });
        }
        finally
        {
            if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private bool CanAccess(Project p)
    {
        if (User.FindFirstValue(ClaimTypes.Role) == Roles.Admin) return true;
        string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(sub, out int callerId) && p.OwnerId == callerId;
    }
}

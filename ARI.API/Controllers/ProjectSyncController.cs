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
[Route("api/v1/projects/{id}/sync")]
[ApiController]
public class ProjectSyncController(ProjectStore store) : ControllerBase
{
    /// <summary>A commit id is 7 to 40 hex digits. Anything else is refused, because the value is put on a git
    /// command line and a crafted one (such as one starting with "--") could change what git does.</summary>
    private static bool IsCommitId(string? value)
        => string.IsNullOrWhiteSpace(value) || System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-fA-F]{7,40}$");

    // ── Status ────────────────────────────────────────────────────────────────────

    [HttpGet("status")]
    public IActionResult GetStatus(string id, [FromQuery] string? clientSha)
    {
        Project? project = store.Get(id);
        if (project is null) return NotFound();
        if (!CanAccess(project)) return StatusCode(403);
        if (project.RootPath is not { } root) return BadRequest(new { error = "Project has no server folder." });
        if (!IsCommitId(clientSha)) return BadRequest(new { error = "clientSha must be a commit id (7 to 40 hex digits)." });

        // Reading the status never changes anything: a project's history is set up when the project is made.
        var (shaCode, serverSha) = ProjectStore.RunGit(root, "rev-parse HEAD");
        serverSha = shaCode == 0 && System.Text.RegularExpressions.Regex.IsMatch(serverSha.Trim(), "^[0-9a-f]{40}$") ? serverSha.Trim() : "";

        int ahead = 0, behind = 0;
        // A commit the server has never seen means the client has work to push (ahead = 1 is "at least one").
        bool clientKnown = string.IsNullOrWhiteSpace(clientSha) || ProjectStore.RunGit(root, $"cat-file -e {clientSha}^{{commit}}").ExitCode == 0;
        if (serverSha.Length == 40 && !clientKnown) ahead = 1;
        if (serverSha.Length == 40 && clientKnown && !string.IsNullOrWhiteSpace(clientSha) && clientSha != serverSha)
        {
            var (_, aheadStr) = ProjectStore.RunGit(root,
                $"rev-list --count {serverSha}..{clientSha}");
            var (_, behindStr) = ProjectStore.RunGit(root,
                $"rev-list --count {clientSha}..{serverSha}");
            int.TryParse(aheadStr.Trim(), out ahead);
            int.TryParse(behindStr.Trim(), out behind);
        }

        // True if the server's working tree physically has files beyond .ariignore/.ariproject.
        // Intentionally checks the filesystem rather than `git ls-files` (the index) — the index
        // can be ahead of the work-tree if a previous reset --hard failed to materialise files.
        bool hasContent = Directory.EnumerateFileSystemEntries(root)
            .Any(e => {
                string name = Path.GetFileName(e);
                return name is not (".ariignore" or ".ariproject" or ".DS_Store");
            });

        return Ok(new { serverSha, clientSha, ahead, behind, hasContent });
    }

    // ── Pull (server → client) ────────────────────────────────────────────────────

    /// <summary>Returns a git bundle containing all commits the client is missing.
    /// If clientSha is omitted, bundles everything (for first-time clone).</summary>
    [HttpGet("pull")]
    public IActionResult Pull(string id, [FromQuery] string? clientSha)
    {
        Project? project = store.Get(id);
        if (project is null) return NotFound();
        if (!CanAccess(project)) return StatusCode(403);
        if (project.RootPath is not { } root) return BadRequest(new { error = "Project has no server folder." });
        if (!IsCommitId(clientSha)) return BadRequest(new { error = "clientSha must be a commit id (7 to 40 hex digits)." });
        if (!Directory.Exists(Path.Combine(root, ".ariproject"))) return Conflict(new { error = "This project has no sync history yet." });

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
    [RequestSizeLimit(200L * 1024 * 1024)]
    public async Task<IActionResult> Push(string id)
    {
        Project? project = store.Get(id);
        if (project is null) return NotFound();
        if (!CanAccess(project)) return StatusCode(403);
        if (project.RootPath is not { } root) return BadRequest(new { error = "Project has no server folder." });

        // One push at a time per project, so two uploads can't interleave their git steps.
        SemaphoreSlim gate = PushLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        string tmp = Path.GetTempFileName();
        try
        {
            using (FileStream fs = System.IO.File.OpenWrite(tmp))
                await Request.Body.CopyToAsync(fs);

            // Fetch into a scratch ref — git refuses to fetch into the currently checked-out branch.
            ProjectStore.RunGit(root, "branch -D ari/incoming"); // clean up any leftover (ignore failure)
            var (fetchCode, fetchOut) = ProjectStore.RunGit(root, $"fetch \"{tmp}\" HEAD:refs/heads/ari/incoming");
            if (fetchCode != 0)
                return BadRequest(new { error = $"Fetch failed: {fetchOut}" });

            // Nothing may be pushed into ARI's own git folder, and no links: a changed git config can make git run a command,
            // and a link could point outside the project.
            (int treeCode, string tree) = ProjectStore.RunGit(root, "ls-tree -r -z refs/heads/ari/incoming");
            bool unsafeTree = treeCode != 0 || tree.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(line =>
                line.StartsWith("120000") || line.StartsWith("160000") || ProjectStore.IsProtectedPath(line[(line.IndexOf('\t') + 1)..]));
            if (unsafeTree)
            {
                ProjectStore.RunGit(root, "branch -D ari/incoming");
                return BadRequest(new { error = "That push contains links, submodules or ARI's own folders, which are not allowed." });
            }

            // The push must build on what the server already has. Otherwise it would silently throw newer server work away.
            bool serverHasHistory = ProjectStore.RunGit(root, "rev-parse --verify --quiet HEAD").ExitCode == 0;
            if (serverHasHistory && ProjectStore.RunGit(root, "merge-base --is-ancestor HEAD refs/heads/ari/incoming").ExitCode != 0)
            {
                ProjectStore.RunGit(root, "branch -D ari/incoming");
                return Conflict(new { error = "The server has changes this push does not include. Pull first, then push again." });
            }

            // Point HEAD at main, fast-forward it to the incoming tip, sync the working tree.
            ProjectStore.RunGit(root, "symbolic-ref HEAD refs/heads/main");
            var (resetCode, resetOut) = ProjectStore.RunGit(root, "reset --hard refs/heads/ari/incoming");
            if (resetCode != 0)
                return StatusCode(500, new { error = $"Reset failed: {resetOut}" });
            ProjectStore.RunGit(root, "branch -D ari/incoming");

            var (_, newSha) = ProjectStore.RunGit(root, "rev-parse HEAD");
            return Ok(new { serverSha = newSha.Trim() });
        }
        finally
        {
            if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
            gate.Release();
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> PushLocks = new();

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private bool CanAccess(Project p)
    {
        if (User.FindFirstValue(ClaimTypes.Role) == Roles.Admin) return true;
        string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(sub, out int callerId) && p.OwnerId == callerId;
    }
}

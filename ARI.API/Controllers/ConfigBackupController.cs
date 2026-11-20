using System.IO.Compression;
using ARI.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>
/// Saves and restores everything about how this install is set up, except the Brain: settings, agents, servers, persona,
/// feedback, plugins, custom tools, accounts and conversations. A restore on a fresh install brings her back as she was; the
/// Brain has its own backups. Admin only (it is under the admin route). Secrets in Keys/ are left out unless asked for.
/// </summary>
[Route("api/v1/admin/config")]
[ApiController]
public class ConfigBackupController : ControllerBase
{
    // Folders under the server's data folder that are saved. Everything else (the Brain, logs, downloaded tools and voice models,
    // projects, scratchpads, previous backups) is either the Brain, rebuildable, or too big to belong in a settings backup.
    private static readonly string[] Folders =
        { "PersistentData", "Personas", "CustomTools", "Plugins", "Scripts", "Templates", "Feedback", "Branding", "LLMConfigs", "HiddenThreads",
          "PersistedThreads", "ChatHistory", "Push", "PromptBackups", "Skills" };

    private static string Root => Paths.ServerDir("");

    /// <summary>The settings backup as a zip. <c>?keys=true</c> also includes the Keys folder (API tokens, secrets).</summary>
    [HttpGet("export")]
    public IActionResult Export([FromQuery] bool keys = false)
    {
        string zip = Path.Combine(Path.GetTempPath(), $"ari-config-{Guid.NewGuid():N}.zip");
        Build(zip, keys);
        FileStream stream = new(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
        return File(stream, "application/zip", $"ARI-Config-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip");
    }

    /// <summary>Puts a settings backup back. The current settings are saved to Backups first, so a wrong restore can be undone.
    /// ARI has to be restarted afterwards: most of this is read once at start-up.</summary>
    [HttpPost("restore")]
    [RequestSizeLimit(1_000_000_000)]
    public async Task<IActionResult> Restore(IFormFile? file)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Send the backup zip as the form field \"file\"." });

        string upload = Path.Combine(Path.GetTempPath(), $"ari-restore-{Guid.NewGuid():N}.zip");
        try
        {
            await using (FileStream fs = System.IO.File.Create(upload)) await file.CopyToAsync(fs);

            string safety = Path.Combine(Root, "Backups", $"ARI-Config-before-restore-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(safety)!);
            Build(safety, includeKeys: true);

            string rootFull = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
            int written = 0;
            using ZipArchive archive = ZipFile.OpenRead(upload);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.Length == 0 && entry.Name.Length == 0) continue;
                string target = Path.GetFullPath(Path.Combine(Root, entry.FullName));
                // Nothing may land outside the server's data folder, and the Brain is never written by a settings restore.
                if (!target.StartsWith(rootFull, StringComparison.Ordinal)) continue;
                string top = entry.FullName.Split('/', '\\')[0];
                if (top.StartsWith("Brain", StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                written++;
            }
            return Ok(new { restored = written, savedBefore = Path.GetFileName(safety), restartNeeded = true });
        }
        catch (InvalidDataException) { return BadRequest(new { error = "That file isn't a valid zip." }); }
        finally { try { System.IO.File.Delete(upload); } catch { } }
    }

    private static void Build(string zipPath, bool includeKeys)
    {
        using ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        // Top-level settings files: .json, .md, .db (accounts, calendar) and their write-ahead logs, with any .bak copies.
        foreach (string file in Directory.EnumerateFiles(Root))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith('.')) continue;
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is ".json" or ".md" or ".db" or ".db-wal" or ".db-shm" or ".txt" or ".env" or ".bak" || name.Contains(".bak-")) AddFile(zip, file, name, includeKeys);
        }

        foreach (string folder in Folders.Concat(includeKeys ? new[] { "Keys" } : Array.Empty<string>()))
        {
            string dir = Path.Combine(Root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                AddFile(zip, file, Path.GetRelativePath(Root, file).Replace('\\', '/'), includeKeys);
        }
    }

    private static void AddFile(ZipArchive zip, string path, string entryName, bool includeKeys)
    {
        // secrets.env sits among the settings files; it only goes in with the keys.
        if (!includeKeys && Path.GetFileName(path).Equals("secrets.env", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using FileStream src = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using Stream dst = entry.Open();
            src.CopyTo(dst);
        }
        catch (IOException) { /* a file in use or gone: leave it out rather than fail the whole backup */ }
        catch (UnauthorizedAccessException) { }
    }
}

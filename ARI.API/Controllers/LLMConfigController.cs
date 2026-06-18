using ARI.API.Data;
using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using System.Text.Json;

namespace ARI.API.Controllers;

[Route("api/cp/llmconfigs")]
[ApiController]
public class LLMConfigController(PersistentData persistentData) : ControllerBase
{
    private static readonly string ConfigsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ari", "Server", "LLMConfigs");

    private static readonly string PersistentDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ari", "Server", "PersistentData");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ── Meta (stored inside each zip as meta.json) ───────────────────────────

    private sealed class ConfigMeta
    {
        public string Name        { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime SavedAt   { get; set; }
    }

    private LLMModule? llm => (LLMModule?)Modules.Llm;

    // ── GET /api/cp/llmconfigs ───────────────────────────────────────────────

    [HttpGet]
    public IActionResult List()
    {
        Directory.CreateDirectory(ConfigsDir);
        var configs = Directory.GetFiles(ConfigsDir, "*.zip")
            .Select(path =>
            {
                try
                {
                    using ZipArchive zip = ZipFile.OpenRead(path);
                    ZipArchiveEntry? metaEntry = zip.GetEntry("meta.json");
                    ConfigMeta meta = metaEntry is not null
                        ? JsonSerializer.Deserialize<ConfigMeta>(
                              ReadEntryText(metaEntry), JsonOpts) ?? new()
                        : new() { Name = Path.GetFileNameWithoutExtension(path) };

                    return new
                    {
                        fileName    = Path.GetFileName(path),
                        name        = meta.Name,
                        description = meta.Description,
                        savedAt     = meta.SavedAt,
                        sizeBytes   = new FileInfo(path).Length,
                    };
                }
                catch { return null; }
            })
            .Where(c => c is not null)
            .OrderByDescending(c => c!.savedAt)
            .ToList();

        return Ok(configs);
    }

    // ── POST /api/cp/llmconfigs/save ─────────────────────────────────────────

    public sealed class SaveConfigRequest
    {
        public string Name        { get; set; } = "";
        public string Description { get; set; } = "";
    }

    [HttpPost("save")]
    public IActionResult Save([FromBody] SaveConfigRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name is required." });

        string safeName = string.Concat(req.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        Directory.CreateDirectory(ConfigsDir);
        string zipPath = Path.Combine(ConfigsDir, safeName + ".zip");

        if (System.IO.File.Exists(zipPath))
            System.IO.File.Delete(zipPath);

        using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            // Data files
            string[] files = ["Servers.json", "Models.json", "Agents.json", "coding_conventions.md"];
            foreach (string file in files)
            {
                string src = Path.Combine(PersistentDir, file);
                if (System.IO.File.Exists(src))
                    zip.CreateEntryFromFile(src, file, CompressionLevel.Optimal);
            }

            // Meta
            ConfigMeta meta = new()
            {
                Name        = req.Name,
                Description = req.Description ?? "",
                SavedAt     = DateTime.UtcNow,
            };
            ZipArchiveEntry metaEntry = zip.CreateEntry("meta.json");
            using StreamWriter sw = new(metaEntry.Open());
            sw.Write(JsonSerializer.Serialize(meta, JsonOpts));
        }

        return Ok(new { ok = true, fileName = Path.GetFileName(zipPath) });
    }

    // ── POST /api/cp/llmconfigs/{fileName}/restore ───────────────────────────

    [HttpPost("{fileName}/restore")]
    public async Task<IActionResult> Restore(string fileName)
    {
        string zipPath = Path.Combine(ConfigsDir, fileName);
        if (!System.IO.File.Exists(zipPath))
            return NotFound(new { error = "Config not found." });

        // Stop all running servers
        if (llm is not null)
        {
            await llm.StopAllServersAsync();
        }

        // Extract over PersistentData (skip meta.json)
        Directory.CreateDirectory(PersistentDir);
        using (ZipArchive zip = ZipFile.OpenRead(zipPath))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.Name == "meta.json") continue;
                string dest = Path.Combine(PersistentDir, entry.Name);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }

        // Restart servers that have BootStartup = true
        if (llm is not null)
        {
            var models   = persistentData.GetModels().ToList();
            var servers  = persistentData.GetServers().ToList();
            llm.ReplaceServers(servers);
            _ = Task.Run(() => llm.StartServersAsync(models, llm.ModelsPath));
        }

        return Ok(new { ok = true });
    }

    // ── DELETE /api/cp/llmconfigs/{fileName} ─────────────────────────────────

    [HttpDelete("{fileName}")]
    public IActionResult Delete(string fileName)
    {
        string zipPath = Path.Combine(ConfigsDir, fileName);
        if (!System.IO.File.Exists(zipPath))
            return NotFound(new { error = "Config not found." });
        System.IO.File.Delete(zipPath);
        return Ok(new { ok = true });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using StreamReader sr = new(entry.Open());
        return sr.ReadToEnd();
    }
}

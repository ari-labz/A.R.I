using ARI.API.Data;
using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARI.API.Controllers;

[Route("api/v1/admin/llmConfigs")]
[ApiController]
public class LLMConfigController(PersistentData persistentData) : ControllerBase
{
    private static readonly string ConfigsDir = Paths.LLMConfigs;

    private static readonly string PersistentDir = Paths.PersistentData;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly string[] ConfigFiles =
    [
        "Servers.json",
        "Models.json",
        "Agents.json",
        "AriConfig.json",
        "Persona.md",
        "PrivacyPolicy.md",
        "coding_conventions.md",
        "Calendar.db",
        "Voice.json",
        "llamacpp.json",
        "username.txt",
    ];

    // ── Meta (stored inside each zip as meta.json) ───────────────────────────

    private sealed class ConfigMeta
    {
        public string Name        { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime SavedAt   { get; set; }
        public string AriVersion  { get; set; } = "";
    }

    private LLMModule? llm => (LLMModule?)Modules.Llm;

    private static string GetAriVersion()
    {
        return Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
    }

    // ── GET /api/v1/admin/llmConfigs ───────────────────────────────────────────────

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

                    List<string> entries = zip.Entries
                        .Where(e => e.Name != "meta.json")
                        .Select(e => e.Name)
                        .ToList();

                    return new
                    {
                        fileName    = Path.GetFileName(path),
                        name        = meta.Name,
                        description = meta.Description,
                        savedAt     = meta.SavedAt,
                        ariVersion  = meta.AriVersion,
                        sizeBytes   = new FileInfo(path).Length,
                        files       = entries,
                    };
                }
                catch { return null; }
            })
            .Where(c => c is not null)
            .OrderByDescending(c => c!.savedAt)
            .ToList();

        return Ok(this.Page(configs));
    }

    // ── POST /api/v1/admin/llmConfigs/save ─────────────────────────────────────────

    public sealed class SaveConfigRequest
    {
        public string Name        { get; set; } = "";
        public string Description { get; set; } = "";
    }

    [HttpPost]
    public IActionResult Save([FromBody] SaveConfigRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name is required." });

        string safeName = string.Concat(req.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Replace("..", "_");
        Directory.CreateDirectory(ConfigsDir);
        string zipPath = Path.Combine(ConfigsDir, safeName + ".zip");

        if (System.IO.File.Exists(zipPath))
            return Conflict(new { error = $"A saved setup called '{req.Name}' already exists. Delete it first or pick another name." });

        using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (string file in ConfigFiles)
            {
                string src = Path.Combine(PersistentDir, file);
                if (!System.IO.File.Exists(src)) continue;

                zip.CreateEntryFromFile(src, file, CompressionLevel.Optimal);
            }

            ConfigMeta meta = new()
            {
                Name        = req.Name,
                Description = req.Description ?? "",
                SavedAt     = DateTime.UtcNow,
                AriVersion  = GetAriVersion(),
            };
            ZipArchiveEntry metaEntry = zip.CreateEntry("meta.json");
            using StreamWriter sw = new(metaEntry.Open());
            sw.Write(JsonSerializer.Serialize(meta, JsonOpts));
        }

        return Ok(new { ok = true, fileName = Path.GetFileName(zipPath) });
    }

    // ── POST /api/v1/admin/llmConfigs/{fileName}/restore ───────────────────────────

    [HttpPost("{id}/restore")]
    public async Task<IActionResult> Restore(string id)
    {
        string fileName = id;
        string zipPath = Path.Combine(ConfigsDir, fileName);
        if (!System.IO.File.Exists(zipPath))
            return NotFound(new { error = "Config not found." });

        // Stop all running servers
        if (llm is not null)
        {
            await llm.StopAllServersAsync();
        }

        // Extract over PersistentData (skip meta.json, merge Discord.json)
        Directory.CreateDirectory(PersistentDir);
        try
        {
        using (ZipArchive zip = ZipFile.OpenRead(zipPath))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.Name == "meta.json") continue;
                if (!ConfigFiles.Contains(entry.Name)) continue;   // only the files a saved setup is made of

                string dest = Path.Combine(PersistentDir, entry.Name);

                entry.ExtractToFile(dest, overwrite: true);
            }
        }
        }
        catch (InvalidDataException)
        {
            // A damaged zip must not leave every server stopped.
            if (llm is not null) _ = Task.Run(() => llm.StartServersAsync(persistentData.GetModels().ToList(), llm.ModelsPath));
            return BadRequest(new { error = "That saved setup is damaged." });
        }

        // Restart servers that have BootStartup = true
        if (llm is not null)
        {
            List<Model> models   = persistentData.GetModels().ToList();
            List<Server> servers  = persistentData.GetServers().ToList();
            llm.ReplaceServers(servers);
            _ = Task.Run(() => llm.StartServersAsync(models, llm.ModelsPath));
        }

        return Ok(new { ok = true });
    }

    // ── GET /api/v1/admin/llmConfigs/{fileName}/contents ──────────────────────────

    [HttpGet("{id}")]
    public IActionResult Contents(string id)
    {
        string fileName = id;
        string zipPath = Path.Combine(ConfigsDir, fileName);
        if (!System.IO.File.Exists(zipPath))
            return NotFound(new { error = "Config not found." });

        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        var contents = zip.Entries
            .Where(e => e.Name != "meta.json")
            .Select(e => new { name = e.Name, size = e.Length })
            .ToList();

        return Ok(contents);
    }

    // ── DELETE /api/v1/admin/llmConfigs/{fileName} ─────────────────────────────────

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        string fileName = id;
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

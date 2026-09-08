using ARI.API.Data;
using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARI.API.Controllers;

[Route("admin/llmconfigs")]
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
        "Discord.json",
        "llamacpp.json",
        "username.txt",
    ];

    private static readonly string[] DiscordTokenFields = ["Token", "BotToken"];

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
            foreach (string file in ConfigFiles)
            {
                string src = Path.Combine(PersistentDir, file);
                if (!System.IO.File.Exists(src)) continue;

                if (file == "Discord.json")
                {
                    string stripped = StripDiscordTokens(System.IO.File.ReadAllText(src));
                    ZipArchiveEntry entry = zip.CreateEntry(file, CompressionLevel.Optimal);
                    using StreamWriter w = new(entry.Open());
                    w.Write(stripped);
                }
                else
                {
                    zip.CreateEntryFromFile(src, file, CompressionLevel.Optimal);
                }
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

        // Extract over PersistentData (skip meta.json, merge Discord.json)
        Directory.CreateDirectory(PersistentDir);
        using (ZipArchive zip = ZipFile.OpenRead(zipPath))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.Name == "meta.json") continue;

                string dest = Path.Combine(PersistentDir, entry.Name);

                if (entry.Name == "Discord.json")
                {
                    MergeDiscordConfig(entry, dest);
                    continue;
                }

                entry.ExtractToFile(dest, overwrite: true);
            }
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

    // ── GET /api/cp/llmconfigs/{fileName}/contents ──────────────────────────

    [HttpGet("{fileName}/contents")]
    public IActionResult Contents(string fileName)
    {
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

    private static string StripDiscordTokens(string json)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            if (node is JsonObject obj)
            {
                foreach (string field in DiscordTokenFields)
                {
                    if (obj.ContainsKey(field))
                        obj[field] = "";
                }
                return obj.ToJsonString(JsonOpts);
            }
        }
        catch { }
        return json;
    }

    private static void MergeDiscordConfig(ZipArchiveEntry entry, string destPath)
    {
        string incoming = ReadEntryText(entry);

        if (!System.IO.File.Exists(destPath))
        {
            System.IO.File.WriteAllText(destPath, incoming);
            return;
        }

        try
        {
            JsonNode? existing = JsonNode.Parse(System.IO.File.ReadAllText(destPath));
            JsonNode? restore  = JsonNode.Parse(incoming);

            if (existing is JsonObject existingObj && restore is JsonObject restoreObj)
            {
                foreach (KeyValuePair<string, JsonNode?> kvp in restoreObj)
                {
                    if (DiscordTokenFields.Contains(kvp.Key))
                        continue;
                    existingObj[kvp.Key] = kvp.Value?.DeepClone();
                }
                System.IO.File.WriteAllText(destPath, existingObj.ToJsonString(JsonOpts));
                return;
            }
        }
        catch { }

        System.IO.File.WriteAllText(destPath, incoming);
    }
}

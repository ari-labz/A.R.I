using ARI.API.Data;
using ARI.Common;
using ARI.ImageGen;
using ARI.LLM;
using ARI.Voice;
using ARI.VoiceSynthesis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ARI.API.Controllers;

public record ModuleInfo(string Key, string Name, string Description, bool Enabled, bool Running, bool Required);

[Route("api/v1/admin/modules")]
[ApiController]
public class ModulesController(ILogger<ModulesController> logger) : ControllerBase
{
    // Description and display order for the Modules tab. The keys match AriConfig.json's
    // "Modules" object, which is the only place a module's on/off state is stored.
    private static readonly (string Key, string Name, string Description, bool Required)[] Catalogue =
    [
        ("LLM", "Language model",
         "Runs the local model servers behind every reply Ari gives. With this off Ari cannot think or hold a conversation.", true),
        ("API", "Web control panel",
         "Serves this control panel and the web chat. Turning it off would leave no way to turn it back on.", true),
        ("Voice", "Voice",
         "Speaks Ari's replies out loud using the active speech engine.", false),
        ("VoiceSynthesis", "Voice training",
         "Installs the speech engines and trains new voices from your recordings.", false),
        ("Listener", "Listener",
         "Transcribes your microphone with Whisper so you can talk to Ari instead of typing.", false),
        ("Brain", "Brain",
         "Stores Ari's long-term memory as an Obsidian vault of notes she can search and add to.", false),
        ("Discord", "Discord",
         "Connects Ari to Discord so she can read and reply to messages and join voice channels.", false),
        ("Calendar", "Calendar",
         "Ari's own calendar of events and reminders — events feed her context, reminders trigger a real message from her at their scheduled time.", false),
    ];

    private static bool IsRunning(string key) => key switch
    {
        "LLM"            => Modules.Llm            is not null,
        "Voice"          => Modules.Voice          is not null,
        // VoiceSynthesisModule is always registered (it's also the training-job tracker), so its
        // presence alone doesn't mean the module is "on" — IsSetupComplete does.
        "VoiceSynthesis" => Modules.VoiceSynthesis?.IsSetupComplete ?? false,
        "Listener"       => Modules.Listener       is not null,
        "Brain"          => Modules.Brain          is not null,
        "Discord"        => Modules.Discord        is not null,
        "Calendar"       => Modules.Calendar       is not null,
        // Answering this request is itself proof the web panel is up.
        "API"            => true,
        _                => false,
    };

    [HttpGet]
    public IActionResult GetModules()
    {
        JsonObject? modules = ReadModulesNode(out string? error);
        if (modules is null)
            return StatusCode(500, new { error });

        List<ModuleInfo> result = Catalogue.Select(m =>
        {
            // A module added after someone's AriConfig.json was first written (Calendar, say) has no
            // node in their file at all. ARI.API can't reference ARI.Core's strongly-typed AriConfig
            // to ask what that module's real default is (that dependency runs the other way), so an
            // absent node instead trusts whatever is actually running right now — a module that is
            // live is definitionally enabled, whatever the file does or doesn't say.
            bool? explicitlySet = modules[m.Key]?["Enabled"]?.GetValue<bool>();
            bool running = IsRunning(m.Key);
            return new ModuleInfo(m.Key, m.Name, m.Description,
                Enabled:  explicitlySet ?? running,
                Running:  running,
                Required: m.Required);
        }).ToList();

        return Ok(new { modules = result });
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> SetEnabled(string id, [FromBody] SetModuleEnabledRequest req)
    {
        string key = id;
        (string Key, string Name, string Description, bool Required) entry = Catalogue.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (entry.Key is null)
            return NotFound(new { error = $"Unknown module '{key}'." });

        if (entry.Required && !req.Enabled)
            return BadRequest(new { error = $"{entry.Name} cannot be turned off — {entry.Description}" });

        string path = Paths.AriConfig;
        JsonObject? modules = ReadModulesNode(out string? error, out JsonNode? root);
        if (modules is null || root is null)
            return StatusCode(500, new { error });

        // A module added to ARI after this file was first written (Calendar, say) has no node here
        // yet — create one rather than failing, so the very first toggle a user makes on a new
        // module doesn't error out.
        if (modules[entry.Key] is not JsonObject moduleNode)
        {
            moduleNode = new JsonObject();
            modules[entry.Key] = moduleNode;
        }

        moduleNode["Enabled"] = req.Enabled;

        try
        {
            // Rewrite through the parsed tree so every other setting — ports, paths and any
            // ${SECRET} placeholders — comes back out exactly as it went in.
            string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Modules] Failed to write {Path}.", path);
            return StatusCode(500, new { error = $"Could not save AriConfig.json: {ex.Message}" });
        }

        // Apply it live if the module knows how to start/stop itself — most do. A null result means
        // it actually happened; any other string is either "can't do this hot" or a genuine failure,
        // and either way the change is still saved above for next boot.
        string? lifecycleResult = Modules.Lifecycle is { } lifecycle
            ? await (req.Enabled ? lifecycle.StartModule(entry.Key) : lifecycle.StopModule(entry.Key))
            : "No running instance to apply this to.";
        bool appliedNow = lifecycleResult is null;

        logger.LogInformation("[Modules] {Module} set to {State} — {Effect}.", entry.Name, req.Enabled ? "enabled" : "disabled",
            appliedNow ? "applied immediately" : $"takes effect on next restart ({lifecycleResult})");

        return Ok(new { key = entry.Key, enabled = req.Enabled, restartRequired = !appliedNow, message = appliedNow ? null : lifecycleResult });
    }

    private static JsonObject? ReadModulesNode(out string? error) => ReadModulesNode(out error, out _);

    private static JsonObject? ReadModulesNode(out string? error, out JsonNode? root)
    {
        error = null;
        root  = null;
        string path = Paths.AriConfig;

        if (!System.IO.File.Exists(path))
        {
            error = $"No AriConfig.json found at {path}.";
            return null;
        }

        try
        {
            root = JsonNode.Parse(System.IO.File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception ex)
        {
            error = $"Could not read AriConfig.json: {ex.Message}";
            return null;
        }

        if (root?["Modules"] is not JsonObject modules)
        {
            error = "AriConfig.json has no \"Modules\" section.";
            return null;
        }

        return modules;
    }
}

public record SetModuleEnabledRequest(bool Enabled);

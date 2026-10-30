using System.Text.Json;
using ARI.Common;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>Control-panel endpoints for the Plugins page. A plugin describes its own settings and actions; this only lists them, applies changes and switches plugins on and off.</summary>
[Route("api/v1/admin/plugins")]
[ApiController]
public class PluginsController : ControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok(new { plugins = PluginRegistry.All.Select(Card) });

    /// <summary>Changes the settings in the body (send only what changes; "enabled" is understood by every plugin). A running plugin restarts so the change takes effect at once.</summary>
    [HttpPatch("{id}/settings")]
    public async Task<IActionResult> SetSettings(string id, [FromBody] Dictionary<string, JsonElement> changes)
    {
        if (PluginRegistry.Find(id) is not { } plugin) return NotFound(new { error = $"There is no plugin called {id}." });
        bool master = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value == Shared.MasterUserId;

        if (plugin.SetValues(changes, master) is { } problem) return BadRequest(new { error = problem });

        string? startError = null;
        await plugin.StopAsync();
        if (plugin.Enabled) startError = await plugin.StartAsync();
        return Ok(new { ok = true, startError, plugin = Card(plugin) });
    }

    [HttpPost("{id}/actions/{key}")]
    public async Task<IActionResult> Run(string id, string key)
    {
        if (PluginRegistry.Find(id) is not { } plugin) return NotFound(new { error = $"There is no plugin called {id}." });
        if (!plugin.Actions.Any(a => a.Key == key)) return NotFound(new { error = $"{plugin.Name} has no action called {key}." });
        return Ok(new { message = await plugin.RunActionAsync(key) });
    }

    /// <summary>Removes a plugin that was written as source (its folder is deleted). The ones that ship with ARI can't be removed.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Remove(string id)
    {
        if (PluginRegistry.Find(id) is not ARI.LLM.ScriptPlugin && !ARI.LLM.CompiledPlugins.IsInstalled(id)) return BadRequest(new { error = "Only plugins that were installed or written here can be removed." });
        return await ARI.LLM.ScriptPlugins.DeleteAsync(id) ? Ok(new { ok = true }) : NotFound(new { error = $"There is no plugin called {id}." });
    }

    /// <summary>Looks at the plugin folders again, for a plugin dropped in by hand.</summary>
    [HttpPost("reload")]
    public async Task<IActionResult> Reload()
    {
        await ARI.LLM.ScriptPlugins.ReloadAsync();
        return Ok(new { plugins = PluginRegistry.All.Select(Card) });
    }

    /// <summary>The plugin packed into one .ari file (a zip of its code, picture and manifest; never its settings). Only plugins written as source can be packed.</summary>
    [HttpGet("{id}/package")]
    public IActionResult Package(string id)
    {
        if (PluginRegistry.Find(id) is not ARI.LLM.ScriptPlugin) return BadRequest(new { error = "Only plugins written as source can be exported from here. A built plugin is packed by its own repo's release." });
        return ARI.LLM.ScriptPlugins.Export(id) is { } bytes ? File(bytes, "application/zip", id + ".ari") : NotFound(new { error = $"There is no plugin called {id}." });
    }

    /// <summary>Installs or updates a plugin from an .ari file sent as the request body. It arrives switched off.</summary>
    [HttpPost("install")]
    [RequestSizeLimit(ARI.LLM.CompiledPlugins.MaxCompiledBytes)]
    public async Task<IActionResult> Install()
    {
        (string? installed, string? error) = await ARI.LLM.ScriptPlugins.InstallAsync(Request.Body);
        if (error is not null) return BadRequest(new { error });
        return Ok(new { ok = true, id = installed, plugins = PluginRegistry.All.Select(Card) });
    }

    // ── The store ────────────────────────────────────────────────────────────

    /// <summary>Every plugin the catalogs list, with what is installed here. ?refresh=true reads the catalogs again.</summary>
    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog([FromQuery] bool refresh = false)
    {
        (List<ARI.LLM.CatalogEntry> entries, List<string> errors) = await ARI.LLM.PluginCatalog.LoadAsync(refresh);
        return Ok(new
        {
            plugins = entries.Select(e => new
            {
                id = e.Plugin.Id, name = e.Plugin.Name, description = e.Plugin.Description, author = e.Plugin.Author, repo = e.Plugin.Repo, featured = e.Plugin.Featured,
                hasIcon = !string.IsNullOrWhiteSpace(e.Plugin.Icon),
                latest = e.Plugin.Versions[0].Version, changelog = e.Plugin.Versions[0].Changelog, size = e.Plugin.Versions[0].Size,
                installed = e.Installed, installedVersion = e.InstalledVersion, updateAvailable = e.UpdateAvailable, source = e.Source,
            }),
            errors,
            sources = ARI.LLM.PluginCatalog.Sources(),
        });
    }

    public sealed record InstallFromCatalog(string Id, string? Version);

    /// <summary>Downloads a plugin from a catalog, checks it and installs it (switched off).</summary>
    [HttpPost("catalog/install")]
    public async Task<IActionResult> CatalogInstall([FromBody] InstallFromCatalog body)
    {
        string? error = await ARI.LLM.PluginCatalog.InstallAsync(body.Id, body.Version);
        return error is null ? Ok(new { ok = true, id = body.Id, plugins = PluginRegistry.All.Select(Card) }) : BadRequest(new { error });
    }

    /// <summary>A catalog plugin's picture, fetched by ARI so the page doesn't depend on where the catalog keeps it.</summary>
    [HttpGet("catalog/{id}/icon")]
    public async Task<IActionResult> CatalogIcon(string id)
    {
        (List<ARI.LLM.CatalogEntry> entries, _) = await ARI.LLM.PluginCatalog.LoadAsync(refresh: false);
        string? link = entries.FirstOrDefault(e => e.Plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Plugin.Icon;
        if (string.IsNullOrWhiteSpace(link)) return NotFound();
        try
        {
            byte[] bytes = Path.IsPathRooted(link) ? await System.IO.File.ReadAllBytesAsync(link) : await new HttpClient { Timeout = TimeSpan.FromSeconds(20) }.GetByteArrayAsync(link);
            return bytes.Length is > 8 and < 1_000_000 ? File(bytes, "image/png") : NotFound();
        }
        catch { return NotFound(); }
    }

    public sealed record CatalogSources(List<string> Extra);

    /// <summary>The catalogs besides the default one: a list of https addresses (or full file paths).</summary>
    [HttpPut("catalog/sources")]
    public IActionResult SetSources([FromBody] CatalogSources body)
    {
        string? problem = ARI.LLM.PluginCatalog.SetExtraSources(body.Extra ?? new());
        return problem is null ? Ok(new { sources = ARI.LLM.PluginCatalog.Sources() }) : BadRequest(new { error = problem });
    }

    /// <summary>The plugin's picture. A plugin without one gets a letter tile from the page.</summary>
    [HttpGet("{id}/icon")]
    public IActionResult Icon(string id) =>
        PluginRegistry.Find(id) is { Icon: { Length: > 0 } png } ? File(png, "image/png") : NotFound();

    /// <summary>One of the plugin's source files as plain text, for the code viewer.</summary>
    [HttpGet("{id}/source")]
    public IActionResult SourceFile(string id, [FromQuery] string file)
    {
        if (PluginRegistry.Find(id) is not { } plugin) return NotFound(new { error = $"There is no plugin called {id}." });
        return plugin.Source.TryGetValue(file ?? "", out string? text) ? Content(text, "text/plain; charset=utf-8") : NotFound(new { error = "That file isn't part of the plugin." });
    }

    private static object Card(IPlugin p) => new
    {
        id = p.Id,
        name = p.Name,
        description = p.Description,
        author = p.Author,
        hasIcon = p.Icon is { Length: > 0 },
        removable = p is ARI.LLM.ScriptPlugin || ARI.LLM.CompiledPlugins.IsInstalled(p.Id),
        exportable = p is ARI.LLM.ScriptPlugin,
        version = ARI.LLM.CompiledPlugins.VersionOf(p.Id) ?? ARI.LLM.ScriptPlugins.VersionOf(p.Id),
        tools = p is ARI.LLM.IToolProvider tp ? tp.Tools.Select(t => new { name = t.Name, description = t.Description }) : [],
        toolsDescription = (p as ARI.LLM.IToolProvider)?.ToolsDescription,
        source = p.Source.Select(f => new { name = f.Key, lines = f.Value.Count(c => c == (char)10) + 1 }),
        enabled = p.Enabled,
        state = p.State.ToString().ToLowerInvariant(),
        status = p.Status,
        settings = p.Settings.Select(s => new { key = s.Key, label = s.Label, type = s.Type.ToString().ToLowerInvariant(), help = s.Help, placeholder = s.Placeholder }),
        values = p.GetValues(),
        actions = p.Actions.Select(a => new { key = a.Key, label = a.Label, help = a.Help, confirm = a.Confirm }),
    };
}

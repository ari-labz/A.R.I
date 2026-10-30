using System.Security.Cryptography;
using System.Text.Json;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public sealed record CatalogVersion(string Version, string Url, string Sha256, long? Size = null, string? Changelog = null, string? Date = null);

/// <summary>One plugin as a catalog lists it. The first version in the list is the newest.</summary>
public sealed record CatalogPlugin(string Id, string Name, string Description, string? Author, string? Icon, string? Repo, bool Featured, List<CatalogVersion> Versions);

public sealed record CatalogFile(string? Name, List<CatalogPlugin> Plugins);

/// <summary>A catalog entry as the Plugins page shows it, with what is installed here.</summary>
public sealed record CatalogEntry(CatalogPlugin Plugin, string Source, bool Installed, string? InstalledVersion, bool UpdateAvailable);

/// <summary>
/// The plugin store: catalogs are plain JSON files (one on GitHub by default, more added by URL) that list plugins and where each version's .ari file
/// can be downloaded, with a SHA-256 to check it against. ARI reads them, shows them, and installs from them the same way it installs a dropped file.
/// A download that doesn't match its checksum is refused. Nothing here runs a plugin; the owner switches it on.
/// </summary>
public static class PluginCatalog
{
    public const string DefaultUrl = "https://raw.githubusercontent.com/ari-labz/A.R.I-Plugins/main/catalog.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static (DateTime At, List<CatalogEntry> Entries, List<string> Errors)? cache;

    private static string SourcesFile => Path.Combine(Paths.PersistentData, "plugin_catalogs.json");

    /// <summary>The catalogs in use: the default one first, then any the owner added.</summary>
    public static List<string> Sources()
    {
        List<string> extra = new();
        try { if (File.Exists(SourcesFile)) extra = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(SourcesFile)) ?? new(); } catch { }
        return new[] { DefaultUrl }.Concat(extra.Where(s => !string.Equals(s, DefaultUrl, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Saves the owner's extra catalogs (the default one is always there). Returns why one was refused, or null.</summary>
    public static string? SetExtraSources(IEnumerable<string> urls)
    {
        List<string> clean = new();
        foreach (string raw in urls.Select(u => u.Trim()).Where(u => u.Length > 0))
        {
            if (!(raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(raw)))
                return $"\"{raw}\" isn't an https address or a full file path.";
            if (!clean.Contains(raw, StringComparer.OrdinalIgnoreCase)) clean.Add(raw);
        }
        File.WriteAllText(SourcesFile, JsonSerializer.Serialize(clean, Json));
        cache = null;
        return null;
    }

    public static List<string> ExtraSources() => Sources().Skip(1).ToList();

    /// <summary>Every plugin in every catalog, newest first within featured and not. A catalog that can't be reached is reported, not fatal.</summary>
    public static async Task<(List<CatalogEntry> Entries, List<string> Errors)> LoadAsync(bool refresh)
    {
        await gate.WaitAsync();
        try
        {
            if (!refresh && cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(5)) return (Annotate(c.Entries), c.Errors);

            List<CatalogEntry> all = new();
            List<string> errors = new();
            foreach (string source in Sources())
            {
                try
                {
                    string text = await ReadTextAsync(source);
                    CatalogFile? file = JsonSerializer.Deserialize<CatalogFile>(text, Json);
                    foreach (CatalogPlugin p in file?.Plugins ?? new())
                    {
                        if (string.IsNullOrWhiteSpace(p.Id) || p.Versions is not { Count: > 0 }) continue;
                        if (all.Any(e => e.Plugin.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase))) continue;   // the first catalog to list an id wins
                        all.Add(new CatalogEntry(p with { Icon = Resolve(source, p.Icon), Versions = p.Versions.Select(v => v with { Url = Resolve(source, v.Url) ?? v.Url }).ToList() }, source, false, null, false));
                    }
                }
                catch (Exception e)
                {
                    errors.Add($"{source}: {(e is HttpRequestException ? "couldn't be reached" : e is JsonException ? "isn't a valid catalog" : e.Message)}");
                    Shared.Logger.LogInformation("[PluginCatalog] {Source} failed: {Message}", source, e.Message);
                }
            }
            cache = (DateTime.UtcNow, all, errors);
            return (Annotate(all), errors);
        }
        finally { gate.Release(); }
    }

    private static List<CatalogEntry> Annotate(List<CatalogEntry> entries) => entries
        .Select(e =>
        {
            string? have = PluginRegistry.Find(e.Plugin.Id) is null ? null : (CompiledPlugins.VersionOf(e.Plugin.Id) ?? ScriptPlugins.VersionOf(e.Plugin.Id) ?? "");
            bool installed = PluginRegistry.Find(e.Plugin.Id) is not null;
            return e with { Installed = installed, InstalledVersion = have, UpdateAvailable = installed && !string.IsNullOrEmpty(have) && have != e.Plugin.Versions[0].Version };
        })
        .OrderByDescending(e => e.Plugin.Featured).ThenBy(e => e.Plugin.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Downloads a version (the newest if none is named), checks its checksum and installs it. Returns null on success, otherwise why not.</summary>
    public static async Task<string?> InstallAsync(string id, string? version)
    {
        (List<CatalogEntry> entries, _) = await LoadAsync(refresh: false);
        CatalogEntry? entry = entries.FirstOrDefault(e => e.Plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return $"No catalog lists a plugin called {id}.";
        CatalogVersion? v = version is null ? entry.Plugin.Versions[0] : entry.Plugin.Versions.FirstOrDefault(x => x.Version == version);
        if (v is null) return $"{entry.Plugin.Name} has no version {version}.";
        if (!v.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !Path.IsPathRooted(v.Url)) return "That download isn't an https address.";
        if (string.IsNullOrWhiteSpace(v.Sha256)) return "That version has no checksum, so it can't be checked, and ARI won't install it.";

        string temp = Path.Combine(Path.GetTempPath(), "ari-download-" + Guid.NewGuid().ToString("N") + ".ari");
        try
        {
            try
            {
                await using Stream src = Path.IsPathRooted(v.Url) ? File.OpenRead(v.Url) : await Http.GetStreamAsync(v.Url);
                await using FileStream dst = File.Create(temp);
                await src.CopyToAsync(dst);
            }
            catch (Exception e) { return $"The download failed: {e.Message}"; }

            string actual;
            await using (FileStream check = File.OpenRead(temp)) actual = Convert.ToHexString(await SHA256.HashDataAsync(check));
            if (!actual.Equals(v.Sha256, StringComparison.OrdinalIgnoreCase)) return "The download doesn't match the checksum in the catalog, so it was thrown away.";

            await using FileStream package = File.OpenRead(temp);
            (string? installed, string? error) = await ScriptPlugins.InstallAsync(package);
            if (error is not null) return error;
            if (!string.Equals(installed, entry.Plugin.Id, StringComparison.OrdinalIgnoreCase)) return $"The package is for \"{installed}\", not {entry.Plugin.Id}.";
            return null;
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    private static async Task<string> ReadTextAsync(string source) =>
        Path.IsPathRooted(source) ? await File.ReadAllTextAsync(source) : await Http.GetStringAsync(source);

    /// <summary>A relative link in a catalog is relative to the catalog itself, which lets a catalog and its files sit together in one folder.</summary>
    private static string? Resolve(string source, string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        if (link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(link)) return link;
        return Path.IsPathRooted(source)
            ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, link))
            : new Uri(new Uri(source), link).ToString();
    }
}

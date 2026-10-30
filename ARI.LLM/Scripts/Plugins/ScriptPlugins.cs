using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ARI.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public sealed record PluginManifest(string Id, string? Name, string? Description, string? Author, string? Version, string? Kind = null, string? Entry = null);

public sealed record ScriptPluginInfo(string Id, string? Name, bool Compiled, List<string> Files, List<ToolDiagnostic> Diagnostics, bool Enabled, List<string> Tools);

/// <summary>
/// Plugins written as source: each is a folder under Plugins/ in the server's data folder, holding .cs files with one [Plugin] class and any
/// number of [Tool] classes. Saving compiles the folder with Roslyn into its own collectible assembly and puts the plugin on the Plugins page
/// at once. A plugin that doesn't compile is reported, never fatal, and the last good version keeps running. Every new plugin starts switched off.
/// </summary>
public static class ScriptPlugins
{
    private sealed class Loaded
    {
        internal required ScriptPlugin Plugin;
        internal required AssemblyLoadContext Alc;
        internal required string Hash;
    }

    private static readonly SemaphoreSlim gate = new(1, 1);
    private static readonly ConcurrentDictionary<string, Loaded> byId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, List<ToolDiagnostic>> failed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex IdRx = new(@"^[a-z][a-z0-9_]{1,40}$", RegexOptions.Compiled);
    private static readonly Regex FileRx = new(@"^[A-Za-z0-9_\-]{1,60}\.cs$", RegexOptions.Compiled);
    private static readonly Regex ToolNameRx = new(@"^[a-z][a-z0-9_]{1,40}$", RegexOptions.Compiled);
    private static int generation;

    public static string Folder => Path.Combine(Paths.PersistentData, "Plugins");

    // ── Start-up ─────────────────────────────────────────────────────────────

    /// <summary>Compiles every plugin folder, puts the plugins on the list, and starts the ones the owner left switched on. Call it once the rest of ARI is up.</summary>
    public static async Task StartAsync()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            await ReloadAsync();
            Shared.Logger.LogInformation("[ScriptPlugins] {Count} plugin(s) loaded from {Dir}.", byId.Count, Folder);
        }
        catch (Exception e) { Shared.Logger.LogError(e, "[ScriptPlugins] Couldn't start; script plugins are unavailable."); }
    }

    /// <summary>Looks at the plugin folders again: compiles new or changed ones, drops ones whose folder is gone. For a plugin dropped in by hand.</summary>
    public static async Task ReloadAsync()
    {
        Directory.CreateDirectory(Folder);
        HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in Directory.GetDirectories(Folder))
        {
            string id = Path.GetFileName(dir);
            if (!IdRx.IsMatch(id)) continue;
            present.Add(id);
            if (CompiledPlugins.IsCompiledFolder(dir)) continue;   // loaded by CompiledPlugins
            await LoadAsync(id, onlyIfChanged: true);
        }
        foreach (string gone in byId.Keys.Where(k => !present.Contains(k)).ToList()) await RemoveAsync(gone);
        await CompiledPlugins.ReloadAsync();
    }

    // ── What Ari and the control panel do ────────────────────────────────────

    public static List<ScriptPluginInfo> List()
    {
        Directory.CreateDirectory(Folder);
        return Directory.GetDirectories(Folder).Where(d => !CompiledPlugins.IsCompiledFolder(d)).Select(Path.GetFileName).Where(id => id is not null && IdRx.IsMatch(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).Select(id => Info(id!)).ToList();
    }

    public static string? ReadCode(string id, string file)
    {
        string path = Path.Combine(Folder, id, file);
        return IdRx.IsMatch(id) && FileRx.IsMatch(file) && File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Writes one file of a plugin, compiles the whole folder and, if it compiled, swaps the new version in. Returns the result either way.</summary>
    public static async Task<ScriptPluginInfo> SaveAsync(string id, string file, string code)
    {
        if (!IdRx.IsMatch(id)) throw new ArgumentException("A plugin's id is lowercase letters, digits and underscores, starting with a letter, like \"weather\".");
        if (!FileRx.IsMatch(file)) throw new ArgumentException("A file name uses letters, numbers, - and _, and ends in .cs.");
        Directory.CreateDirectory(Path.Combine(Folder, id));
        await File.WriteAllTextAsync(Path.Combine(Folder, id, file), code);
        await LoadAsync(id, onlyIfChanged: false);
        return Info(id);
    }

    public static async Task<bool> DeleteAsync(string id)
    {
        if (!IdRx.IsMatch(id) || !Directory.Exists(Path.Combine(Folder, id))) return false;
        if (CompiledPlugins.IsInstalled(id)) { await CompiledPlugins.RemoveAsync(id); Directory.Delete(Path.Combine(Folder, id), recursive: true); return true; }
        await RemoveAsync(id);
        Directory.Delete(Path.Combine(Folder, id), recursive: true);
        return true;
    }

    /// <summary>Calls one of a plugin's tools with JSON arguments, whether or not the plugin is on, so Ari can try a tool before the owner switches it on.</summary>
    public static async Task<string> TestCallAsync(string id, string tool, string argsJson, CancellationToken ct)
    {
        if (!byId.TryGetValue(id, out Loaded? l)) return $"[Error: there is no plugin called {id}.]";
        if (l.Plugin.Tools.FirstOrDefault(t => t.Name.Equals(tool, StringComparison.OrdinalIgnoreCase)) is not { } provided) return $"[Error: {id} has no tool called {tool}. Its tools: {string.Join(", ", l.Plugin.Tools.Select(t => t.Name))}.]";
        Tool? instance = provided.Create(new Thread(ThreadPipeline.Dialogue, "plugin-test") { Internal = true });
        if (instance is null) return "[Error: that tool can't be built here.]";
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        ToolResult result = await instance.Execute(argsJson);
        return result.Text;
    }

    // ── .ari files ───────────────────────────────────────────────────────────
    // A plugin packed into one file: a zip holding manifest.json, icon.png (optional) and the .cs files. Never settings.json, so a plugin
    // can be handed on without the owner's tokens and choices.

    public const string IconFile = "icon.png";
    public const string ManifestFile = "manifest.json";
    private const int MaxFiles = 60, MaxPackageBytes = 5_000_000, MaxFilesCompiled = 2000;

    public static PluginManifest? ReadManifest(string folder)
    {
        try
        {
            string path = Path.Combine(folder, ManifestFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
        }
        catch { return null; }
    }

    public static string? VersionOf(string id) => ReadManifest(Path.Combine(Folder, id))?.Version;

    /// <summary>The plugin as the bytes of an .ari file, or null when there is no such plugin.</summary>
    public static byte[]? Export(string id)
    {
        string dir = Path.Combine(Folder, id);
        if (!IdRx.IsMatch(id) || !Directory.Exists(dir) || !byId.TryGetValue(id, out Loaded? l)) return null;
        PluginManifest old = ReadManifest(dir) ?? new(id, null, null, null, null);
        PluginManifest manifest = new(id, l.Plugin.Name, l.Plugin.Description, old.Author, old.Version ?? "1.0");
        using MemoryStream ms = new();
        using (ZipArchive zip = new(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] data) { using Stream s = zip.CreateEntry(name, CompressionLevel.Optimal).Open(); s.Write(data); }
            Add(ManifestFile, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(Path.Combine(dir, IconFile))) Add(IconFile, File.ReadAllBytes(Path.Combine(dir, IconFile)));
            foreach (string f in Directory.GetFiles(dir, "*.cs").OrderBy(f => f)) Add(Path.GetFileName(f), File.ReadAllBytes(f));
        }
        return ms.ToArray();
    }

    /// <summary>Installs (or updates) a plugin from an .ari file. The owner's settings for an existing plugin are kept. A package that doesn't compile or load changes nothing.</summary>
    public static async Task<(string? Id, string? Error)> InstallAsync(Stream package)
    {
        string temp = Path.Combine(Path.GetTempPath(), "ari-package-" + Guid.NewGuid().ToString("N") + ".ari");
        try
        {
            await using (FileStream fs = File.Create(temp))
            {
                byte[] buf = new byte[81920]; long total = 0; int n;
                while ((n = await package.ReadAsync(buf)) > 0)
                {
                    if ((total += n) > CompiledPlugins.MaxCompiledBytes) return (null, "That file is too big to be a plugin.");
                    await fs.WriteAsync(buf.AsMemory(0, n));
                }
            }

            ZipArchive zip;
            try { zip = ZipFile.OpenRead(temp); }
            catch (InvalidDataException) { return (null, "That isn't a plugin package (an .ari file is a zip)."); }
            using (zip)
            {
                ZipArchiveEntry? manifestEntry = zip.GetEntry(ManifestFile);
                if (manifestEntry is null) return (null, "That package has no manifest.json.");
                PluginManifest? manifest;
                try { using Stream ms = manifestEntry.Open(); manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(ms, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
                catch { return (null, "manifest.json couldn't be read."); }
                if (manifest is null || !IdRx.IsMatch(manifest.Id ?? "")) return (null, "manifest.json needs an id: lowercase letters, digits and underscores, like \"weather\".");
                if (zip.Entries.Count > MaxFilesCompiled) return (null, "That package has too many files.");

                if (string.Equals(manifest.Kind, "compiled", StringComparison.OrdinalIgnoreCase))
                {
                    if (PluginRegistry.Find(manifest.Id!) is { } clash && !CompiledPlugins.IsInstalled(manifest.Id!) && clash is not ScriptPlugin)
                        return (null, $"{clash.Name} is built into this copy of ARI and can't be replaced by a package yet.");
                    string? problem = await CompiledPlugins.InstallAsync(zip, manifest);
                    return problem is null ? (manifest.Id, null) : (null, problem);
                }
                return await InstallSourceAsync(zip, manifest);
            }
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    private static async Task<(string? Id, string? Error)> InstallSourceAsync(ZipArchive zip, PluginManifest manifest)
    {
        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
        if (zip.Entries.Count > MaxFiles) return (null, "That package has too many files.");
        long total = 0;
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;
            string name = e.FullName;
            if (name != ManifestFile && name != IconFile && !FileRx.IsMatch(name)) return (null, $"A plugin package holds only manifest.json, icon.png and .cs files. This one has \"{name}\".");
            if ((total += e.Length) > MaxPackageBytes) return (null, "That package unpacks to too much.");
            using Stream s = e.Open();
            using MemoryStream copy = new();
            await s.CopyToAsync(copy);
            files[name] = copy.ToArray();
        }
        if (!files.Keys.Any(k => k.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))) return (null, "That package has no code in it.");
        if (files.TryGetValue(IconFile, out byte[]? icon) && !CompiledPlugins.IsPng(icon)) return (null, "icon.png has to be a PNG under 500 KB.");

        string id = manifest.Id!, dir = Path.Combine(Folder, id);
        if (PluginRegistry.Find(id) is { } existing && existing is not ScriptPlugin) return (null, $"{existing.Name} ships with ARI and can't be replaced by a package.");

        // Put the old version aside, so a package that fails to compile leaves things as they were.
        string backup = Path.Combine(Path.GetTempPath(), "ari-plugin-" + Guid.NewGuid().ToString("N"));
        bool had = Directory.Exists(dir);
        Directory.CreateDirectory(dir);
        if (had)
        {
            Directory.CreateDirectory(backup);
            foreach (string f in Directory.GetFiles(dir).Where(f => !Path.GetFileName(f).Equals("settings.json", StringComparison.OrdinalIgnoreCase))) File.Copy(f, Path.Combine(backup, Path.GetFileName(f)));
        }
        try
        {
            foreach (string f in Directory.GetFiles(dir).Where(f => !Path.GetFileName(f).Equals("settings.json", StringComparison.OrdinalIgnoreCase))) File.Delete(f);
            foreach ((string name, byte[] data) in files) await File.WriteAllBytesAsync(Path.Combine(dir, name), data);
            await LoadAsync(id, onlyIfChanged: false);
            if (byId.ContainsKey(id) && !failed.ContainsKey(id)) return (id, null);

            string why = failed.TryGetValue(id, out List<ToolDiagnostic>? d) ? d.FirstOrDefault(x => x.Severity == "Error")?.Message ?? "it didn't compile" : "it didn't load";
            if (had)
            {
                foreach (string f in Directory.GetFiles(dir).Where(f => !Path.GetFileName(f).Equals("settings.json", StringComparison.OrdinalIgnoreCase))) File.Delete(f);
                foreach (string f in Directory.GetFiles(backup)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
                await LoadAsync(id, onlyIfChanged: false);
            }
            else { await RemoveAsync(id); Directory.Delete(dir, recursive: true); }
            return (null, $"The plugin didn't install: {why}");
        }
        finally { try { if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true); } catch { } }
    }

    private static ScriptPluginInfo Info(string id)
    {
        string dir = Path.Combine(Folder, id);
        List<string> files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.cs").Select(f => Path.GetFileName(f)!).OrderBy(f => f).ToList() : new();
        byId.TryGetValue(id, out Loaded? l);
        failed.TryGetValue(id, out List<ToolDiagnostic>? d);
        return new ScriptPluginInfo(id, l?.Plugin.Name, l is not null, files, d ?? new(), l?.Plugin.Enabled ?? false, l?.Plugin.Tools.Select(t => t.Name).ToList() ?? new());
    }

    // ── Compile and load ─────────────────────────────────────────────────────

    private static async Task RemoveAsync(string id)
    {
        await gate.WaitAsync();
        try
        {
            failed.TryRemove(id, out _);
            if (byId.TryRemove(id, out Loaded? old))
            {
                await old.Plugin.StopAsync();
                PluginRegistry.Unregister(id);
                try { old.Alc.Unload(); } catch { }
            }
        }
        finally { gate.Release(); }
    }

    private static async Task LoadAsync(string id, bool onlyIfChanged)
    {
        await gate.WaitAsync();
        try
        {
            string dir = Path.Combine(Folder, id);
            SortedDictionary<string, string> sources = new(StringComparer.OrdinalIgnoreCase);
            foreach (string f in Directory.GetFiles(dir, "*.cs")) sources[Path.GetFileName(f)] = File.ReadAllText(f);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", sources.Select(s => s.Key + "\n" + s.Value)))));
            if (onlyIfChanged && byId.TryGetValue(id, out Loaded? same) && same.Hash == hash) return;

            (Loaded? loaded, List<ToolDiagnostic> diags) = Build(id, dir, sources, hash);
            if (loaded is null)
            {
                failed[id] = diags;   // keep the last good version running, if there was one
                Shared.Logger.LogWarning("[ScriptPlugins] {Id} didn't compile: {First}", id, diags.FirstOrDefault()?.Message ?? "unknown");
                return;
            }
            failed.TryRemove(id, out _);

            bool wasRunning = false;
            if (byId.TryGetValue(id, out Loaded? old)) { wasRunning = old.Plugin.State == PluginState.Running; await old.Plugin.StopAsync(); }
            byId[id] = loaded;
            PluginRegistry.Register(loaded.Plugin);       // replaces the old one and refreshes the tool groups
            if (old is not null) { try { old.Alc.Unload(); } catch { } }
            if (wasRunning || loaded.Plugin.Enabled) await loaded.Plugin.StartAsync();   // a plugin the owner left switched on comes back on
            Shared.Logger.LogInformation("[ScriptPlugins] {Id} loaded ({Tools} tool(s)).", id, loaded.Plugin.Tools.Count);
        }
        finally { gate.Release(); }
    }

    private static (Loaded?, List<ToolDiagnostic>) Build(string id, string dir, SortedDictionary<string, string> sources, string hash)
    {
        List<ToolDiagnostic> diags = new();
        if (sources.Count == 0) return (null, [new ToolDiagnostic("Error", "PL0001", 1, 1, "The folder has no .cs files.")]);

        string assemblyName = $"ScriptPlugin_{id}_{Interlocked.Increment(ref generation)}";
        CSharpParseOptions parse = new(LanguageVersion.Latest);
        List<SyntaxTree> trees = sources.Select(s => CSharpSyntaxTree.ParseText(s.Value, parse, s.Key)).ToList();
        trees.Add(CSharpSyntaxTree.ParseText(CustomTools.GlobalUsings + " global using ARI.Common;", parse, "usings.cs"));
        trees.Add(CSharpSyntaxTree.ParseText(CustomTools.AccessShim, parse, "access.cs"));
        CSharpCompilation compilation = CSharpCompilation.Create(assemblyName, trees, CustomTools.References(),
            CustomTools.IgnoreAccessibility(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Disable)));

        using MemoryStream ms = new();
        var result = compilation.Emit(ms);
        foreach (Diagnostic d in result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning))
        {
            LinePosition at = d.Location.GetLineSpan().StartLinePosition;
            string where = d.Location.SourceTree?.FilePath is { } path ? $"[{Path.GetFileName(path)}] " : "";
            diags.Add(new ToolDiagnostic(d.Severity.ToString(), d.Id, d.Location.SourceTree is null ? 1 : at.Line + 1, at.Character + 1, where + d.GetMessage()));
        }
        if (!result.Success)
        {
            if (!diags.Any(d => d.Severity == "Error")) diags.Add(new ToolDiagnostic("Error", "PL0001", 1, 1, "The plugin didn't compile."));
            return (null, diags);
        }

        AssemblyLoadContext alc = new(assemblyName, isCollectible: true);
        ms.Position = 0;
        Assembly asm = alc.LoadFromStream(ms);

        List<Type> plugins = asm.GetTypes().Where(t => !t.IsAbstract && typeof(ScriptPlugin).IsAssignableFrom(t) && t.GetCustomAttribute<PluginAttribute>() is not null).ToList();
        List<Type> tools = asm.GetTypes().Where(t => !t.IsAbstract && typeof(Tool).IsAssignableFrom(t) && t.GetCustomAttribute<ToolAttribute>() is not null).ToList();

        string? problem = plugins.Count switch
        {
            0 => "No class is marked [Plugin(\"Name\", \"description\")] and inherits ScriptPlugin.",
            > 1 => "A plugin has one [Plugin] class. Found " + string.Join(", ", plugins.Select(t => t.Name)) + ".",
            _ => null,
        };
        if (problem is null && plugins[0].GetConstructor(Type.EmptyTypes) is null) problem = $"{plugins[0].Name} needs a public constructor with no parameters.";
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (Type t in tools)
        {
            if (problem is not null) break;
            string name = t.GetCustomAttribute<ToolAttribute>()!.Name;
            if (!ToolNameRx.IsMatch(name)) problem = $"The tool name \"{name}\" should be lowercase letters, digits and underscores, starting with a letter.";
            else if (!names.Add(name)) problem = $"Two tools in this plugin are called \"{name}\".";
            else if (ToolFactories.IsCoreBuiltIn(name)) problem = $"\"{name}\" is the name of a built-in tool. Pick another.";
            else if (CustomTools.IsCustom(name)) problem = $"A custom tool is already called \"{name}\".";
            else if (PluginTools.Find(name) is { } other && !other.Plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) problem = $"The {other.Plugin.Name} plugin already has a tool called \"{name}\".";
            else if (t.GetConstructor(Type.EmptyTypes) is null) problem = $"{t.Name} needs a public constructor with no parameters.";
        }
        if (problem is not null)
        {
            diags.Add(new ToolDiagnostic("Error", "PL0002", 1, 1, problem));
            try { alc.Unload(); } catch { }
            return (null, diags);
        }

        ScriptPlugin plugin;
        try { plugin = (ScriptPlugin)Activator.CreateInstance(plugins[0])!; }
        catch (Exception e)
        {
            diags.Add(new ToolDiagnostic("Error", "PL0003", 1, 1, $"Couldn't create the plugin: {(e.InnerException ?? e).Message}"));
            try { alc.Unload(); } catch { }
            return (null, diags);
        }
        plugin.Init(id, dir);
        foreach (Type t in tools)
        {
            ToolAttribute attr = t.GetCustomAttribute<ToolAttribute>()!;
            object schema = ToolParams.Schema(attr.Name, attr.Description, ToolParams.Describe(t));
            int timeout = Math.Clamp(attr.TimeoutSeconds, 1, 3600);
            plugin.ProvidedTools.Add(new ProvidedTool(attr.Name, attr.Description, thread => new PluginToolHost(attr.Name, t, schema, attr.Access, timeout, thread)));
        }
        return (new Loaded { Plugin = plugin, Alc = alc, Hash = hash }, diags);
    }

    /// <summary>What gets registered on a thread for a plugin's tool: it makes a fresh instance of the tool's class for every call, so calls never share fields.</summary>
    private sealed class PluginToolHost(string name, Type type, object schema, ToolAccess access, int timeoutSeconds, Thread thread) : Tool
    {
        internal override string Name => name;
        internal override object Schema => schema;
        internal override ToolAccess Access => access;

        internal override Task<ToolResult> Execute(string argsJson)
        {
            Tool instance;
            try { instance = (Tool)Activator.CreateInstance(type)!; }
            catch (Exception e) { return Task.FromResult<ToolResult>($"[Error: couldn't start {name}: {e.Message}]"); }
            instance.CallThread = thread;
            return instance.RunGuarded(argsJson, timeoutSeconds, name);
        }
    }
}

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>What ARI gives a compiled plugin when it creates it: the few things every plugin needs from the host, so a plugin never reaches for ARI's internals to get them.</summary>
public interface IPluginHost
{
    ILoggerFactory Loggers { get; }
    LLMModule? Llm { get; }
    CancellationToken Stopping { get; }
    /// <summary>The folder that holds every installed plugin's own folder (settings.json, icon.png).</summary>
    string PluginsFolder { get; }
    /// <summary>Keeps a background task from being lost (its failures get logged).</summary>
    void Track(Task task);
}

/// <summary>
/// Plugins that arrive already built, from an .ari file with <c>"kind": "compiled"</c>: a folder <c>Plugins/&lt;id&gt;/</c> holding manifest.json, icon.png and a
/// <c>plugin/</c> folder with the plugin's .dll, the libraries it needs and any native files. Each one runs in its own load context, so its libraries
/// can't clash with ARI's or another plugin's, but it shares ARI's own assemblies so the two can talk. The plugin class either takes an
/// <see cref="IPluginHost"/> in its constructor or has none.
/// </summary>
public static class CompiledPlugins
{
    public const long MaxCompiledBytes = 250_000_000;
    private const int MaxIconBytes = 512_000;
    private static readonly string[] AllowedExtensions = [".dll", ".json", ".so", ".dylib", ".dat"];

    private sealed class Loaded { internal required IPlugin Plugin; internal required AssemblyLoadContext Alc; internal required string Version; }

    private static readonly ConcurrentDictionary<string, Loaded> byId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Set once at start-up by the program that hosts ARI.</summary>
    public static IPluginHost? Host { get; set; }

    public static bool IsCompiledFolder(string dir) => ScriptPlugins.ReadManifest(dir)?.Kind is { } k && k.Equals("compiled", StringComparison.OrdinalIgnoreCase);
    public static bool IsInstalled(string id) => Directory.Exists(Path.Combine(ScriptPlugins.Folder, id)) && IsCompiledFolder(Path.Combine(ScriptPlugins.Folder, id));

    public static bool IsPng(byte[] b) => b.Length is >= 8 and <= MaxIconBytes && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

    /// <summary>Loads every compiled plugin folder that isn't loaded yet and starts the ones the owner left switched on.</summary>
    public static async Task ReloadAsync()
    {
        if (!Directory.Exists(ScriptPlugins.Folder)) return;
        foreach (string dir in Directory.GetDirectories(ScriptPlugins.Folder).Where(IsCompiledFolder))
        {
            string id = Path.GetFileName(dir);
            if (byId.ContainsKey(id)) continue;
            if (Load(id) is { } problem) Shared.Logger.LogWarning("[CompiledPlugins] {Id} didn't load: {Problem}", id, problem);
        }
        foreach (Loaded l in byId.Values) if (l.Plugin.Enabled && l.Plugin.State != PluginState.Running) await l.Plugin.StartAsync();
    }

    /// <summary>Unpacks a compiled package into its folder, loads it and, if the owner had it on, starts it. A package that won't load leaves the old version in place.</summary>
    public static async Task<string?> InstallAsync(ZipArchive zip, PluginManifest manifest)
    {
        string id = manifest.Id;
        string entry = manifest.Entry ?? "";
        if (!entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || entry.Contains('/') || entry.Contains('\\')) return "manifest.json needs an \"entry\": the plugin's .dll inside plugin/, like \"ARI.Plugin.Discord.dll\".";
        if (zip.GetEntry("plugin/" + entry) is null) return $"The package doesn't contain plugin/{entry}.";

        string folder = Path.Combine(ScriptPlugins.Folder, id);
        string staging = folder + ".staging-" + Guid.NewGuid().ToString("N")[..8];
        string backup = folder + ".backup-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(staging);
        try
        {
            long total = 0;
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                string name = e.FullName.Replace('\\', '/');
                bool ok = name == ScriptPlugins.ManifestFile || name == ScriptPlugins.IconFile
                    || (name.StartsWith("plugin/", StringComparison.Ordinal) && AllowedExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()));
                if (!ok) return $"A compiled plugin package holds manifest.json, icon.png and, under plugin/, only .dll, .json and native library files. This one has \"{name}\".";
                if (name.Split('/').Any(part => part is ".." or "." or "")) return $"\"{name}\" isn't a valid path.";
                if ((total += e.Length) > MaxCompiledBytes) return "That package unpacks to too much.";
                string dest = Path.GetFullPath(Path.Combine(staging, name));
                if (!dest.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return $"\"{name}\" isn't a valid path.";
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest);
            }
            if (File.Exists(Path.Combine(staging, ScriptPlugins.IconFile)) && !IsPng(File.ReadAllBytes(Path.Combine(staging, ScriptPlugins.IconFile)))) return "icon.png has to be a PNG under 500 KB.";

            bool had = Directory.Exists(folder);
            bool wasOn = byId.TryGetValue(id, out Loaded? old) && old.Plugin.State == PluginState.Running;
            if (old is not null) await RemoveAsync(id);
            if (had)
            {
                string settings = Path.Combine(folder, "settings.json");
                if (File.Exists(settings)) File.Copy(settings, Path.Combine(staging, "settings.json"), overwrite: true);
                Directory.Move(folder, backup);
            }
            Directory.Move(staging, folder);

            string? problem = Load(id);
            if (problem is not null)
            {
                await RemoveAsync(id);
                Directory.Delete(folder, recursive: true);
                if (had) { Directory.Move(backup, folder); Load(id); }
                return $"The plugin didn't install: {problem}";
            }
            if (byId.TryGetValue(id, out Loaded? now) && (wasOn || now.Plugin.Enabled)) await now.Plugin.StartAsync();
            return null;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            try { if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true); } catch { }
        }
    }

    public static async Task RemoveAsync(string id)
    {
        await gate.WaitAsync();
        try
        {
            if (byId.TryRemove(id, out Loaded? l))
            {
                try { await l.Plugin.StopAsync(); } catch { }
                PluginRegistry.Unregister(id);
                try { l.Alc.Unload(); } catch { }
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>Loads the plugin in this folder and puts it on the list. Returns null on success, otherwise why not.</summary>
    private static string? Load(string id)
    {
        string folder = Path.Combine(ScriptPlugins.Folder, id);
        PluginManifest? manifest = ScriptPlugins.ReadManifest(folder);
        if (manifest?.Entry is not { Length: > 0 } entry) return "manifest.json has no entry.";
        string dll = Path.Combine(folder, "plugin", entry);
        if (!File.Exists(dll)) return $"plugin/{entry} is missing.";
        if (Host is null) return "The host isn't ready.";

        PluginLoadContext alc = new(id, dll);
        try
        {
            Assembly asm = alc.LoadFromAssemblyPath(dll);
            List<Type> types = asm.GetTypes().Where(t => !t.IsAbstract && typeof(IPlugin).IsAssignableFrom(t)).ToList();
            if (types.Count != 1) { alc.Unload(); return types.Count == 0 ? "No class in the plugin implements IPlugin." : "More than one class implements IPlugin."; }
            Type type = types[0];
            object? instance = type.GetConstructor([typeof(IPluginHost)]) is { } withHost ? withHost.Invoke([Host])
                : type.GetConstructor(Type.EmptyTypes) is { } plain ? plain.Invoke([])
                : null;
            if (instance is not IPlugin plugin) { alc.Unload(); return $"{type.Name} needs a constructor taking IPluginHost, or none."; }
            if (!plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) { alc.Unload(); return $"The plugin says its id is \"{plugin.Id}\" but the package says \"{id}\"."; }

            byId[id] = new Loaded { Plugin = plugin, Alc = alc, Version = manifest.Version ?? "" };
            PluginRegistry.Register(plugin);
            Shared.Logger.LogInformation("[CompiledPlugins] {Id} {Version} loaded.", id, manifest.Version);
            return null;
        }
        catch (Exception e)
        {
            try { alc.Unload(); } catch { }
            return (e.InnerException ?? e).Message;
        }
    }

    /// <summary>The version a compiled plugin was installed as, for the Plugins page.</summary>
    public static string? VersionOf(string id) => byId.TryGetValue(id, out Loaded? l) ? l.Version : ScriptPlugins.ReadManifest(Path.Combine(ScriptPlugins.Folder, id))?.Version;

    /// <summary>
    /// A plugin's own load context: it finds the plugin's libraries and native files in its folder, but anything ARI already has loaded
    /// (ARI.Common, ARI.LLM, the framework) comes from ARI, so a type means the same thing on both sides.
    /// </summary>
    private sealed class PluginLoadContext(string id, string entryPath) : AssemblyLoadContext("plugin_" + id, isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(entryPath);
        private readonly string dir = Path.GetDirectoryName(entryPath)!;

        protected override Assembly? Load(AssemblyName name)
        {
            // ARI's own copy wins, but only when it is new enough: a plugin that needs a newer version of a library than the one ARI
            // happens to have loaded gets its own.
            if (AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == name.Name && (name.Version is null || a.GetName().Version >= name.Version))) return null;
            string? path = resolver.ResolveAssemblyToPath(name);
            if (path is null && File.Exists(Path.Combine(dir, name.Name + ".dll"))) path = Path.Combine(dir, name.Name + ".dll");
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

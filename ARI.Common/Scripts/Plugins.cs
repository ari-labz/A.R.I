using System.Collections.Concurrent;
using System.Text.Json;

namespace ARI.Common;

public enum PluginSettingType
{
    /// <summary>One line of text.</summary>
    Text,
    /// <summary>A password or token. Never sent to the browser: the panel only learns whether one is saved.</summary>
    Secret,
    /// <summary>On or off.</summary>
    Toggle,
    /// <summary>A list of numeric IDs, kept as strings end to end because 64-bit IDs lose precision as JavaScript numbers.</summary>
    IdList,
}

/// <summary>One setting a plugin asks for. The control panel draws the form from these, so a plugin never ships any UI.</summary>
public sealed record PluginSetting(string Key, string Label, PluginSettingType Type, string? Help = null, string? Placeholder = null);

/// <summary>A button on the plugin's card that does something once, such as deleting its recent messages.</summary>
public sealed record PluginAction(string Key, string Label, string Help, bool Confirm = false);

public enum PluginState { Stopped, Running, Error }

/// <summary>
/// A plugin is the orchestrator for one outside service or capability: it owns the link to it, decides what arrives and what it means,
/// keeps its own settings, and is what its tools talk to. ARI only lists plugins, switches them on and off, and draws their settings.
/// </summary>
public interface IPlugin
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    /// <summary>"first-party" for the ones that ship with ARI, otherwise who wrote it.</summary>
    string Author { get; }

    /// <summary>The plugin's picture as a PNG, or null for a plain letter tile.</summary>
    byte[]? Icon { get; }

    /// <summary>Its source files by name, for the read-only code viewer on the plugin's page. Empty if it doesn't ship any.</summary>
    IReadOnlyDictionary<string, string> Source { get; }

    IReadOnlyList<PluginSetting> Settings { get; }
    IReadOnlyList<PluginAction> Actions { get; }

    /// <summary>Whether the owner has switched it on. Stored by the plugin with the rest of its settings.</summary>
    bool Enabled { get; }
    PluginState State { get; }
    /// <summary>A short line for the card: what it is connected to, or why it isn't.</summary>
    string? Status { get; }

    /// <summary>Current values by setting key: text as a string, toggles as a bool, ID lists as a string array, secrets as a bool saying whether one is saved.</summary>
    IReadOnlyDictionary<string, object?> GetValues();

    /// <summary>Changes the settings named in <paramref name="changes"/> and leaves the rest. A blank secret keeps the saved one. Returns null on success, otherwise why not.</summary>
    string? SetValues(IReadOnlyDictionary<string, JsonElement> changes, bool callerIsMaster);

    /// <summary>Starts it if it is enabled and can run. Returns null on success, otherwise why not.</summary>
    Task<string?> StartAsync();
    Task StopAsync();

    /// <summary>Runs one of <see cref="Actions"/> and returns what to show the owner.</summary>
    Task<string> RunActionAsync(string key);
}

/// <summary>Every plugin ARI knows about, in the order they were added.</summary>
public static class PluginRegistry
{
    private static readonly ConcurrentDictionary<string, IPlugin> plugins = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> order = new();
    private static readonly object gate = new();

    /// <summary>Raised after a plugin is added or replaced, so things built from the plugin list (the tool groups) can refresh.</summary>
    public static event Action? Changed;

    public static void Register(IPlugin plugin)
    {
        lock (gate) { if (plugins.TryAdd(plugin.Id, plugin)) order.Add(plugin.Id); else plugins[plugin.Id] = plugin; }
        Changed?.Invoke();
    }

    /// <summary>Takes a plugin out of the list, for one that was deleted.</summary>
    public static void Unregister(string id)
    {
        lock (gate) { if (plugins.TryRemove(id, out _)) order.Remove(id); }
        Changed?.Invoke();
    }

    public static IReadOnlyList<IPlugin> All { get { lock (gate) return order.Select(id => plugins[id]).ToList(); } }

    public static IPlugin? Find(string id) => plugins.TryGetValue(id, out IPlugin? p) ? p : null;
}

public static class PluginSource
{
    /// <summary>The source files a plugin project embeds (an EmbeddedResource with a LogicalName starting "source/"), by file name.</summary>
    public static IReadOnlyDictionary<string, string> Embedded(System.Reflection.Assembly assembly)
    {
        SortedDictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("source/", StringComparison.Ordinal)))
        {
            using Stream? stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;
            using StreamReader reader = new(stream);
            files[resource["source/".Length..]] = reader.ReadToEnd();
        }
        return files;
    }
}

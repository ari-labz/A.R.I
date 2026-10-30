using System.Text.Json;
using ARI.Common;

namespace ARI.LLM;

/// <summary>Marks the one class in a script plugin that is the plugin. Its name and description are what the owner sees on the Plugins page.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PluginAttribute(string name, string description) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; } = description;
}

/// <summary>
/// The base class for a plugin written as source and compiled by ARI. A plugin is a folder of .cs files: one class that inherits this
/// (marked [Plugin]) and any number of [Tool] classes, which become the plugin's tools. It keeps its own settings, can run something in the
/// background while it is on, and its tools are in Ari's list only while it is on. It starts switched off: the owner turns it on.
/// </summary>
public abstract class ScriptPlugin : IPlugin, IToolProvider
{
    private string id = "", folder = "";
    private readonly object gate = new();
    private Dictionary<string, JsonElement> values = new();
    private string? error;

    internal List<ProvidedTool> ProvidedTools = new();

    internal void Init(string pluginId, string pluginFolder)
    {
        id = pluginId; folder = pluginFolder;
        try { values = File.Exists(SettingsFile) ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(SettingsFile)) ?? new() : new(); }
        catch { values = new(); }
    }

    private string SettingsFile => Path.Combine(folder, "settings.json");

    /// <summary>The plugin with this id, so a tool in the same plugin can reach its settings and state.</summary>
    public static ScriptPlugin? Get(string id) => PluginRegistry.Find(id) as ScriptPlugin;

    // ── What the owner sees ──────────────────────────────────────────────────

    public string Id => id;
    public string Name => GetType().GetCustomAttributes(typeof(PluginAttribute), false).OfType<PluginAttribute>().FirstOrDefault()?.Name ?? id;
    public string Description => GetType().GetCustomAttributes(typeof(PluginAttribute), false).OfType<PluginAttribute>().FirstOrDefault()?.Description ?? "";
    public string Author => ScriptPlugins.ReadManifest(folder)?.Author is { Length: > 0 } a ? a : "custom";

    /// <summary>The plugin's picture: icon.png in its folder, which travels inside its .ari file.</summary>
    public byte[]? Icon => File.Exists(Path.Combine(folder, ScriptPlugins.IconFile)) ? File.ReadAllBytes(Path.Combine(folder, ScriptPlugins.IconFile)) : null;

    public IReadOnlyDictionary<string, string> Source
    {
        get
        {
            SortedDictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(folder)) foreach (string f in Directory.GetFiles(folder, "*.cs")) files[Path.GetFileName(f)] = File.ReadAllText(f);
            return files;
        }
    }

    public IReadOnlyList<ProvidedTool> Tools => ProvidedTools;
    public virtual string ToolsDescription => Description;

    /// <summary>The settings the owner can edit on the plugin's page. The page draws the form from this.</summary>
    public virtual IReadOnlyList<PluginSetting> Settings => [];

    /// <summary>One-off buttons on the plugin's page. Handle them in <see cref="OnAction"/>.</summary>
    public virtual IReadOnlyList<PluginAction> Actions => [];

    // ── Switching on and off ─────────────────────────────────────────────────

    public bool Enabled => values.TryGetValue("enabled", out JsonElement v) && v.ValueKind == JsonValueKind.True;
    public PluginState State { get; private set; } = PluginState.Stopped;

    /// <summary>Set this while the plugin runs to say what it is doing ("Watching 3 feeds"); it shows under the plugin's name.</summary>
    protected string? StatusText { get; set; }

    public string? Status => State == PluginState.Running ? StatusText ?? "Running" : error ?? (Enabled ? "Not running" : "Switched off");

    /// <summary>Runs when the plugin is switched on. Start whatever has to run in the background here. Optional.</summary>
    protected virtual Task OnStart() => Task.CompletedTask;

    /// <summary>Runs when it is switched off. Stop what OnStart started. Optional.</summary>
    protected virtual Task OnStop() => Task.CompletedTask;

    /// <summary>Runs when the owner presses one of <see cref="Actions"/>. Return what to show them.</summary>
    protected virtual Task<string> OnAction(string key) => Task.FromResult($"{Name} has no action called {key}.");

    public async Task<string?> StartAsync()
    {
        if (!Enabled || State == PluginState.Running) return null;
        error = null;
        try { await OnStart(); State = PluginState.Running; return null; }
        catch (Exception e) { error = e.Message; State = PluginState.Error; return e.Message; }
    }

    public async Task StopAsync()
    {
        error = null;
        if (State != PluginState.Running) { State = PluginState.Stopped; return; }
        try { await OnStop(); } catch (Exception e) { error = e.Message; }
        State = PluginState.Stopped;
    }

    public Task<string> RunActionAsync(string key) => Actions.Any(a => a.Key == key) ? OnAction(key) : Task.FromResult($"{Name} has no action called {key}.");

    // ── Settings ─────────────────────────────────────────────────────────────

    /// <summary>The text the owner typed for a setting.</summary>
    public string Text(string key) => values.TryGetValue(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>The value of a secret setting. Never shown on the page.</summary>
    public string Secret(string key) => Text(key);

    public bool Flag(string key) => values.TryGetValue(key, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    public string[] Ids(string key) => values.TryGetValue(key, out JsonElement v) && v.ValueKind == JsonValueKind.Array
        ? v.EnumerateArray().Select(e => e.ToString()).ToArray() : [];

    public IReadOnlyDictionary<string, object?> GetValues()
    {
        Dictionary<string, object?> result = new();
        foreach (PluginSetting s in Settings)
            result[s.Key] = s.Type switch
            {
                PluginSettingType.Secret => !string.IsNullOrEmpty(Text(s.Key)),
                PluginSettingType.Toggle => Flag(s.Key),
                PluginSettingType.IdList => Ids(s.Key),
                _ => Text(s.Key),
            };
        return result;
    }

    public string? SetValues(IReadOnlyDictionary<string, JsonElement> changes, bool callerIsMaster)
    {
        lock (gate)
        {
            Dictionary<string, JsonElement> next = new(values);
            foreach ((string key, JsonElement value) in changes)
            {
                if (key == "enabled") { next[key] = JsonSerializer.SerializeToElement(value.ValueKind == JsonValueKind.True); continue; }
                PluginSetting? setting = Settings.FirstOrDefault(s => s.Key == key);
                if (setting is null) return $"{Name} has no setting called {key}.";
                switch (setting.Type)
                {
                    case PluginSettingType.Secret:
                        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) next[key] = JsonSerializer.SerializeToElement(value.GetString()!.Trim());
                        break;
                    case PluginSettingType.Text:
                        next[key] = JsonSerializer.SerializeToElement(value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "");
                        break;
                    case PluginSettingType.Toggle:
                        next[key] = JsonSerializer.SerializeToElement(value.ValueKind == JsonValueKind.True);
                        break;
                    case PluginSettingType.IdList:
                    {
                        if (value.ValueKind != JsonValueKind.Array) return $"{setting.Label} must be a list.";
                        List<string> ids = new();
                        foreach (JsonElement item in value.EnumerateArray())
                        {
                            string text = item.ToString().Trim();
                            if (text.Length == 0) continue;
                            if (!ulong.TryParse(text, out _)) return $"{setting.Label} must be numeric IDs.";
                            if (!ids.Contains(text)) ids.Add(text);
                        }
                        next[key] = JsonSerializer.SerializeToElement(ids);
                        break;
                    }
                }
            }
            values = next;
            Directory.CreateDirectory(folder);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
            if (!OperatingSystem.IsWindows()) { try { File.SetUnixFileMode(SettingsFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { } }
            return null;
        }
    }
}

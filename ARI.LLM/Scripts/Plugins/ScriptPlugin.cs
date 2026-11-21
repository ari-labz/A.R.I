using System.Text.Json;
using ARI.Common;
using Microsoft.Extensions.Logging;

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

    // ── Hooks: ARI tells a running plugin what is happening, so it can do something alongside, or instead ─────────
    // A plugin overrides only the ones it needs. The On… hooks add a step: they run in the background, in HookOrder, and never hold
    // anything up. RewriteUserMessage and ReplacesMemorySweep replace a step: ARI waits for them (briefly), in HookOrder. A hook that
    // throws is logged and ignored.

    /// <summary>Where this plugin's hooks run among other plugins' hooks on the same step: lower first. Ties go by plugin id.</summary>
    public virtual int HookOrder => 0;

    /// <summary>Replaces the text of a message before Ari reads it. Return null to leave it alone. With several plugins, each sees the
    /// text the one before it returned. ARI waits up to five seconds, then carries on without it.</summary>
    public virtual Task<string?> RewriteUserMessage(HookMessage message) => Task.FromResult<string?>(null);

    /// <summary>Return true to handle a memory sweep instead of Engram (do the plugin's own sweep inside). The first plugin to say true
    /// takes it and Engram doesn't run for that sweep.</summary>
    public virtual Task<bool> ReplacesMemorySweep(HookSweep sweep) => Task.FromResult(false);

    /// <summary>Someone sent a message in a chat. <see cref="HookMessage.IsOwner"/> says whether it was the owner.</summary>
    public virtual Task OnUserMessage(HookMessage message) => Task.CompletedTask;

    /// <summary>Ari finished a reply in a chat.</summary>
    public virtual Task OnReplyFinished(HookReply reply) => Task.CompletedTask;

    /// <summary>A memory sweep of a conversation has just finished.</summary>
    public virtual Task OnMemorySweep(HookSweep sweep) => Task.CompletedTask;

    /// <summary>Asks ARI's model a question (on the reflector's server) and returns its answer. For plugins that need judgement.</summary>
    public static Task<string> AskModel(string text, CancellationToken ct = default) => PluginHooks.Ask?.Invoke(text, ct) ?? Task.FromResult("");

    /// <summary>This plugin's own folder, for files it keeps (never sent anywhere).</summary>
    protected string PluginFolder => folder;

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

public sealed record HookMessage(string ThreadKey, string Text, bool IsOwner, DateTime Sent);
public sealed record HookReply(string ThreadKey, string Text);
public sealed record HookSweep(string ThreadKey, string Trigger);

/// <summary>Calls the hooks of every running script plugin. Fire and forget: a slow or failing plugin never delays Ari.</summary>
internal static class PluginHooks
{
    internal static Func<string, CancellationToken, Task<string>>? Ask { get; set; }

    private static readonly TimeSpan RewriteWait = TimeSpan.FromSeconds(5);

    private static List<ScriptPlugin> Running() => PluginRegistry.All.OfType<ScriptPlugin>()
        .Where(p => p.State == PluginState.Running).OrderBy(p => p.HookOrder).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();

    private static bool Overrides(ScriptPlugin p, string method) => p.GetType().GetMethod(method)?.DeclaringType != typeof(ScriptPlugin);

    /// <summary>Runs an add-on hook on every running plugin, one after another in HookOrder, in the background.</summary>
    internal static void Fire(Func<ScriptPlugin, Task> hook)
    {
        List<ScriptPlugin> plugins = Running();
        if (plugins.Count == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (ScriptPlugin plugin in plugins)
            {
                try { await hook(plugin); }
                catch (Exception ex) { Shared.Logger.LogWarning("[Plugin {Id}] hook failed: {Err}", plugin.Id, ex.Message); }
            }
        });
    }

    /// <summary>The message after every plugin that rewrites messages has had its turn, in HookOrder.</summary>
    internal static async Task<string> RewriteUserMessage(HookMessage message)
    {
        string text = message.Text;
        foreach (ScriptPlugin plugin in Running().Where(p => Overrides(p, nameof(ScriptPlugin.RewriteUserMessage))))
        {
            try
            {
                Task<string?> rewrite = plugin.RewriteUserMessage(message with { Text = text });
                if (await Task.WhenAny(rewrite, Task.Delay(RewriteWait)) != rewrite) { Shared.Logger.LogWarning("[Plugin {Id}] took too long to rewrite a message; carried on without it.", plugin.Id); continue; }
                if (await rewrite is { } changed && changed != text) { text = changed; Shared.Logger.LogInformation("[Plugin {Id}] rewrote a message.", plugin.Id); }
            }
            catch (Exception ex) { Shared.Logger.LogWarning("[Plugin {Id}] rewrite failed: {Err}", plugin.Id, ex.Message); }
        }
        return text;
    }

    /// <summary>The id of the plugin that took this sweep instead of Engram, or null when Engram should run.</summary>
    internal static async Task<string?> TakeMemorySweep(HookSweep sweep)
    {
        foreach (ScriptPlugin plugin in Running().Where(p => Overrides(p, nameof(ScriptPlugin.ReplacesMemorySweep))))
        {
            try { if (await plugin.ReplacesMemorySweep(sweep)) return plugin.Id; }
            catch (Exception ex) { Shared.Logger.LogWarning("[Plugin {Id}] sweep hook failed: {Err}", plugin.Id, ex.Message); }
        }
        return null;
    }
}

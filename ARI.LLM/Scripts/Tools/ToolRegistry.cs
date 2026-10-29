using System.Collections.Concurrent;
using System.Text.Json;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>What the user changed about one tool. Only deviations from the defaults are stored: enabled, not async,
/// in the group ToolGroups.json puts it in.</summary>
public sealed record ToolSetting(bool Enabled = true, bool Async = false, string? Group = null);

/// <summary>A change to one tool. Null fields are left alone. Group: "@default" restores the shipped group, "" takes
/// it out of every group, anything else moves it there.</summary>
public sealed record ToolPatch(bool? Enabled, bool? Async, string? Group);

public sealed record ToolInfo(string Name, string Description, string Group, string DefaultGroup, string Source,
                              string? Module, bool Enabled, bool Async);

public sealed record ToolGroupInfo(string Name, string Description, bool Custom, bool Enabled);

/// <summary>
/// The user's per-tool settings (enabled, async, group), kept in the persistent data folder and applied everywhere a
/// tool is built, listed or run. The catalogue of tools themselves still comes from <see cref="ToolFactories"/>;
/// this only decides which are offered and how they behave. See Documentation/Server/Tool-Settings.md.
/// </summary>
public static class ToolRegistry
{
    public const string DefaultGroup = "@default";

    private static readonly object Lock = new();
    private static Dictionary<string, ToolSetting>? settings;
    private static readonly ConcurrentDictionary<string, string> descriptions = new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath => Path.Combine(Paths.PersistentData, "tool_settings.json");

    // Tools that only exist while a module is on. Shown in the control panel so it's clear why one is missing.
    private static readonly Dictionary<string, string> ModuleOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["search_brain"] = "Brain", ["recall_memory"] = "Brain", ["memories_on"] = "Brain", ["create_memory"] = "Brain",
        ["edit_memory"] = "Brain", ["delete_memory"] = "Brain", ["neighbours"] = "Brain",
        ["create_event"] = "Calendar", ["create_reminder"] = "Calendar", ["list_events"] = "Calendar", ["delete_entry"] = "Calendar",
        ["discord_list_voice_channels"] = "Discord", ["discord_join_voice_channel"] = "Discord", ["discord_leave_voice_channel"] = "Discord",
        ["discord_dm_user"] = "Discord", ["discord_close_dm"] = "Discord",
        ["generate_image"] = "ImageGen", ["present_image"] = "ImageGen",
    };

    private static Dictionary<string, ToolSetting> Load()
    {
        lock (Lock)
        {
            if (settings is not null) return settings;
            try
            {
                settings = File.Exists(FilePath)
                    ? new(JsonSerializer.Deserialize<Dictionary<string, ToolSetting>>(File.ReadAllText(FilePath)) ?? new(), StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e)
            {
                Shared.Logger.LogWarning(e, "[Tools] Could not read {Path}; using defaults.", FilePath);
                settings = new(StringComparer.OrdinalIgnoreCase);
            }
            return settings;
        }
    }

    private static void Save()
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    internal static bool IsEnabled(string name)
    {
        lock (Lock) return !Load().TryGetValue(name, out ToolSetting? s) || s.Enabled;
    }

    internal static bool IsAsync(string name)
    {
        lock (Lock) return Load().TryGetValue(name, out ToolSetting? s) && s.Async;
    }

    /// <summary>The group the user moved a tool to: null when untouched, "" when taken out of every group.</summary>
    internal static string? GroupOverride(string name)
    {
        lock (Lock) return Load().TryGetValue(name, out ToolSetting? s) ? s.Group : null;
    }

    /// <summary>Applies a change and rebuilds the group table. Returns false when the tool is unknown.</summary>
    public static bool Update(string name, ToolPatch patch)
    {
        if (!ToolFactories.AllNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        lock (Lock)
        {
            Dictionary<string, ToolSetting> all = Load();
            ToolSetting current = all.TryGetValue(name, out ToolSetting? s) ? s : new ToolSetting();
            string? group = patch.Group switch { null => current.Group, DefaultGroup => null, { } g => g.Trim() };
            ToolSetting next = current with { Enabled = patch.Enabled ?? current.Enabled, Async = patch.Async ?? current.Async, Group = group };
            if (next == new ToolSetting()) all.Remove(name); else all[name] = next;
            Save();
        }
        ToolGroups.Rebuild();
        return true;
    }

    /// <summary>Switches every tool in a group on or off. Returns false when no such group exists.</summary>
    public static bool SetGroupEnabled(string group, bool enabled)
    {
        if (!ToolGroups.TryGet(group, out ToolGroupDef def)) return false;
        lock (Lock)
        {
            Dictionary<string, ToolSetting> all = Load();
            foreach (string name in def.Tools)
            {
                ToolSetting next = (all.TryGetValue(name, out ToolSetting? s) ? s : new ToolSetting()) with { Enabled = enabled };
                if (next == new ToolSetting()) all.Remove(name); else all[name] = next;
            }
            Save();
        }
        return true;
    }

    /// <summary>Everything the control panel's Tools tab shows.</summary>
    public static (List<ToolInfo> Tools, List<ToolGroupInfo> Groups) Catalogue()
    {
        List<ToolInfo> tools = new();
        foreach (string name in ToolFactories.AllNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            string? group = GroupOverride(name) ?? ToolGroups.BaseGroupOf(name);
            tools.Add(new ToolInfo(name, Describe(name), group ?? "", ToolGroups.BaseGroupOf(name) ?? "", "builtin",
                                   ModuleOf.GetValueOrDefault(name), IsEnabled(name), IsAsync(name)));
        }

        List<ToolGroupInfo> groups = ToolGroups.AllNames().OrderBy(g => g, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            ToolGroups.TryGet(g, out ToolGroupDef def);
            return new ToolGroupInfo(g, def.Description, !ToolGroups.IsShipped(g), def.Tools.Any(IsEnabled));
        }).ToList();
        return (tools, groups);
    }

    // A tool's own description needs a built instance, and many need context (a bound project) a catalogue
    // doesn't have, so those come back empty and the panel falls back to the group's description.
    private static string Describe(string name) => descriptions.GetOrAdd(name, n =>
    {
        try
        {
            Thread scratch = new(ThreadPipeline.Dialogue, "tool-catalogue") { Internal = true };
            return ToolFactories.TryBuild(n, scratch, out Tool tool, ignoreSettings: true) ? tool.SchemaDescription : "";
        }
        catch { return ""; }
    });
}

/// <summary>
/// Runs an async tool in the background. The model gets an immediate "started" message and carries on; the real
/// result comes back through <see cref="ParentReports"/>, which folds it into a running turn at its next step or
/// wakes an idle conversation — the same path subagent results take.
/// </summary>
internal static class AsyncToolJobs
{
    private static readonly TimeSpan MaxRunTime = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, int> nextId = new();

    internal static ToolResult Start(Thread thread, Tool tool, string argsJson)
    {
        string label = $"{tool.Name} #{nextId.AddOrUpdate(thread.Key, 1, (_, n) => n + 1)}";
        string key   = thread.Key;

        _ = Task.Run(async () =>
        {
            string text;
            try
            {
                ToolResult result = await tool.Execute(argsJson).WaitAsync(MaxRunTime);
                result = tool.PostRun(thread, argsJson, result);
                text = result.Kind == ToolResult.ContentKind.Text
                    ? result.Text
                    : "it produced something that isn't text, which can't be handed over after the fact";
            }
            catch (TimeoutException) { text = $"it ran past its {MaxRunTime.TotalMinutes:0}-minute limit and was given up on"; }
            catch (Exception e)
            {
                Shared.Logger.LogWarning(e, "[Tools] ({Thread}) async {Job} failed", key, label);
                text = $"it failed: {e.Message}";
            }
            ParentReports.Send(key, $"tool:{label}", $"[Result of {label}, which you started in the background]\n{text}");
        });

        return $"{label} started in the background. Carry on — its result will reach you when it finishes, so don't wait for it.";
    }
}

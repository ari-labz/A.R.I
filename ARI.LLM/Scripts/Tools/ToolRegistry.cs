using System.Collections.Concurrent;
using System.Text.Json;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>What the user changed about one tool. Only deviations from the defaults are stored: enabled, not async,
/// in the group ToolGroups.json puts it in.</summary>
public sealed record ToolSetting(bool Enabled = true, bool Async = false, string? Group = null, bool? Pinned = null);

/// <summary>A change to one tool. Null fields are left alone. Group: "@default" restores the shipped group, "" takes
/// it out of every group, anything else moves it there.</summary>
public sealed record ToolPatch(bool? Enabled, bool? Async, string? Group, bool? Pinned = null);

public sealed record ToolInfo(string Name, string Description, string Group, string DefaultGroup, string Source,
                              string? Module, bool Enabled, bool Async, bool Pinned);

public sealed record ToolParam(string Name, string Type, bool Required, string Description);

/// <summary>Everything the Tools tab's sidebar shows for one tool. Code is the tool's source: editable for a custom
/// tool (CodeFile is its script), read-only for a built-in.</summary>
public sealed record ToolDetail(string Name, string Description, string Source, string? Module, string Group, bool Enabled, bool Async, bool Pinned,
                                List<ToolParam> Parameters, string? Code, string? CodeFile, bool Editable);

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
    // Registered on every conversation outside the tool factories (the meta tools and the two always-on reflexes).
    internal static readonly string[] SystemTools = ["list_tools", "request_tools", "tool_search", "remember", "hand_to_coder"];

    private static readonly Dictionary<string, string> ModuleOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["search_brain"] = "Brain", ["recall_memory"] = "Brain", ["memories_on"] = "Brain", ["create_memory"] = "Brain",
        ["edit_memory"] = "Brain", ["delete_memory"] = "Brain", ["neighbours"] = "Brain",
        ["create_event"] = "Calendar", ["create_reminder"] = "Calendar", ["list_events"] = "Calendar", ["delete_entry"] = "Calendar",
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

    // The tools every conversation starts with in hand, so Ari doesn't have to find them first. The owner can pin or unpin any tool.
    private static readonly HashSet<string> DefaultPinned = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "write_file", "edit_file", "list_directory", "search_files", "find_files", "deliver_file", "jobs",
        "search_web", "fetch_page", "shell", "run_code", "todo", "show_visual",
    };

    internal static bool IsPinned(string name)
    {
        lock (Lock) return Load().TryGetValue(name, out ToolSetting? s) && s.Pinned is { } p ? p : DefaultPinned.Contains(name);
    }

    /// <summary>Pinned tools, in a fixed order so the prompt Ari starts with is the same every time and stays cached.</summary>
    internal static List<string> PinnedNames()
        => ToolFactories.AllNames().Where(IsPinned).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    internal static bool IsAsync(string name)
    {
        lock (Lock) return Load().TryGetValue(name, out ToolSetting? s) && s.Async;
    }

    /// <summary>The group the user moved a tool to: null when untouched, "" when taken out of every group.</summary>
    internal static string? GroupOverride(string name)
    {
        lock (Lock) return Load().TryGetValue(name, out ToolSetting? s) ? s.Group : null;
    }

    /// <summary>Gives a tool the group and async setting its author chose, the first time ARI sees it. After that the
    /// user's own choices in the control panel are the only ones that count.</summary>
    internal static void SeedIfNew(string name, string? group, bool async)
    {
        lock (Lock)
        {
            Dictionary<string, ToolSetting> all = Load();
            if (all.ContainsKey(name) || (group is null && !async)) return;
            all[name] = new ToolSetting(Async: async, Group: group);
            Save();
        }
    }

    internal static string GroupOf(string name) => GroupOverride(name) ?? ToolGroups.BaseGroupOf(name) ?? "";
    internal static string DescriptionFor(string name) => Describe(name);

    internal static void ForgetDescription(string name) => descriptions.TryRemove(name, out _);

    /// <summary>Applies a change and rebuilds the group table. Returns false when the tool is unknown.</summary>
    public static bool Update(string name, ToolPatch patch)
    {
        if (!ToolFactories.AllNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        lock (Lock)
        {
            Dictionary<string, ToolSetting> all = Load();
            ToolSetting current = all.TryGetValue(name, out ToolSetting? s) ? s : new ToolSetting();
            string? group = patch.Group switch { null => current.Group, DefaultGroup => null, { } g => g.Trim() };
            ToolSetting next = current with { Enabled = patch.Enabled ?? current.Enabled, Async = patch.Async ?? current.Async, Group = group, Pinned = patch.Pinned ?? current.Pinned };
            if (next == new ToolSetting()) all.Remove(name); else all[name] = next;
            Save();
        }
        ToolGroups.Rebuild();
        return true;
    }

    /// <summary>Applies one change to many tools at once, saving once. Unknown names are skipped; returns how many changed.</summary>
    public static int UpdateMany(IEnumerable<string> names, ToolPatch patch)
    {
        HashSet<string> known = new(ToolFactories.AllNames(), StringComparer.OrdinalIgnoreCase);
        int n = 0;
        lock (Lock)
        {
            Dictionary<string, ToolSetting> all = Load();
            foreach (string name in names.Where(known.Contains).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ToolSetting current = all.TryGetValue(name, out ToolSetting? s) ? s : new ToolSetting();
                string? group = patch.Group switch { null => current.Group, DefaultGroup => null, { } g => g.Trim() };
                ToolSetting next = current with { Enabled = patch.Enabled ?? current.Enabled, Async = patch.Async ?? current.Async, Group = group, Pinned = patch.Pinned ?? current.Pinned };
                if (next == new ToolSetting()) all.Remove(name); else all[name] = next;
                n++;
            }
            if (n > 0) Save();
        }
        if (n > 0) ToolGroups.Rebuild();
        return n;
    }

    /// <summary>One tool in full, for the sidebar. Null when there's no such tool.</summary>
    public static ToolDetail? Detail(string name)
    {
        bool system = SystemTools.Contains(name);
        if (!system && !ToolFactories.AllNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        bool custom = CustomTools.IsCustom(name);
        List<ToolParam> ps = new();
        string description = Describe(name);
        try
        {
            if (ToolFactories.TryBuild(name, Scratch(), out Tool tool, ignoreSettings: true))
            {
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(tool.Schema));
                if (doc.RootElement.TryGetProperty("function", out System.Text.Json.JsonElement fn) && fn.TryGetProperty("parameters", out System.Text.Json.JsonElement pr)
                    && pr.TryGetProperty("properties", out System.Text.Json.JsonElement props))
                {
                    HashSet<string> required = pr.TryGetProperty("required", out System.Text.Json.JsonElement req)
                        ? req.EnumerateArray().Select(e => e.GetString() ?? "").ToHashSet() : new();
                    foreach (System.Text.Json.JsonProperty p in props.EnumerateObject())
                        ps.Add(new ToolParam(p.Name, p.Value.TryGetProperty("type", out System.Text.Json.JsonElement t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString()! : "string",
                                             required.Contains(p.Name), p.Value.TryGetProperty("description", out System.Text.Json.JsonElement d) ? d.GetString() ?? "" : ""));
                }
            }
        }
        catch { }

        string? code = null, codeFile = null;
        if (custom && CustomTools.FileOf(name) is { } file) { codeFile = file; code = CustomTools.ReadCode(file); }
        else if (ToolSources.For(name) is { } src) { codeFile = src.File; code = src.Code; }

        return new ToolDetail(name, description, system ? "system" : custom ? "custom" : "builtin", ModuleOf.GetValueOrDefault(name),
            GroupOverride(name) ?? ToolGroups.BaseGroupOf(name) ?? "", system || IsEnabled(name), !system && IsAsync(name), system || IsPinned(name), ps, code, codeFile, custom);
    }

    private static readonly Lazy<string> EmptyDir = new(() => Directory.CreateTempSubdirectory("ari-tools-").FullName);

    // A thread to build tools on when no conversation is involved. It has an empty folder bound so the file tools
    // can describe themselves.
    private static Thread Scratch() => new(ThreadPipeline.Dialogue, "tool-catalogue") { Internal = true, FilesystemRoot = EmptyDir.Value };

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
            tools.Add(new ToolInfo(name, Describe(name), group ?? "", ToolGroups.BaseGroupOf(name) ?? "", CustomTools.IsCustom(name) ? "custom" : "builtin",
                                   ModuleOf.GetValueOrDefault(name), IsEnabled(name), IsAsync(name), IsPinned(name)));
        }
        // Tools every conversation gets by itself. They're shown so everything Ari can use is visible, but they can't be switched off.
        foreach (string name in SystemTools)
            tools.Add(new ToolInfo(name, Describe(name), "", "", "system", null, true, false, true));

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
            if (PluginTools.Find(n) is { } provided) return provided.Tool.Description;
            string built = ToolFactories.TryBuild(n, Scratch(), out Tool tool, ignoreSettings: true) ? tool.SchemaDescription : "";
            return built.Length > 0 ? built : ToolSources.DescriptionOf(n);
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
    internal sealed class Job
    {
        internal required string Label, ToolName, Thread; internal required DateTime Started; internal required CancellationTokenSource Cancel;
        internal string State = "running"; internal string? Result; internal DateTime? Finished;
        internal string Summary => $"{Label}: {State}" + (Finished is { } f ? $" after {(f - Started).TotalSeconds:0.#}s" : $" for {(DateTime.Now - Started).TotalSeconds:0}s");
    }

    private static readonly TimeSpan MaxRunTime = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, int> nextId = new();
    private static readonly ConcurrentDictionary<string, List<Job>> jobs = new();

    /// <summary>The background jobs this conversation has started, oldest first (finished ones stay listed so their result can be read again).</summary>
    internal static List<Job> For(string threadKey) { lock (jobs) return jobs.TryGetValue(threadKey, out List<Job>? l) ? l.ToList() : new(); }

    internal static Job? Find(string threadKey, string id) =>
        For(threadKey).FirstOrDefault(j => j.Label.Equals(id, StringComparison.OrdinalIgnoreCase) || j.Label.EndsWith("#" + id.TrimStart('#'), StringComparison.OrdinalIgnoreCase) && (id.All(char.IsDigit) || id.StartsWith('#')));

    internal static string Cancel(Job job)
    {
        if (job.State != "running") return $"{job.Label} had already {job.State}.";
        job.State = "cancelled"; job.Finished = DateTime.Now; job.Cancel.Cancel();
        return $"{job.Label} cancelled; its result will be ignored.";
    }

    internal static ToolResult Start(Thread thread, Tool tool, string argsJson)
    {
        string label = $"{tool.Name} #{nextId.AddOrUpdate(thread.Key, 1, (_, n) => n + 1)}";
        string key   = thread.Key;
        Job job = new() { Label = label, ToolName = tool.Name, Thread = key, Started = DateTime.Now, Cancel = new CancellationTokenSource() };
        lock (jobs) { if (!jobs.TryGetValue(key, out List<Job>? l)) jobs[key] = l = new(); l.Add(job); if (l.Count > 30) l.RemoveAt(0); }

        _ = Task.Run(async () =>
        {
            string text;
            try
            {
                ToolResult result = await tool.Execute(argsJson).WaitAsync(MaxRunTime, job.Cancel.Token);
                result = tool.PostRun(thread, argsJson, result);
                text = result.Kind == ToolResult.ContentKind.Text
                    ? result.Text
                    : "it produced something that isn't text, which can't be handed over after the fact";
                job.State = "finished";
            }
            catch (OperationCanceledException) { job.Result = "cancelled"; return; }   // already reported to her by the cancel itself
            catch (TimeoutException) { text = $"it ran past its {MaxRunTime.TotalMinutes:0}-minute limit and was given up on"; job.State = "failed"; }
            catch (Exception e)
            {
                Shared.Logger.LogWarning(e, "[Tools] ({Thread}) async {Job} failed", key, label);
                text = $"it failed: {e.Message}"; job.State = "failed";
            }
            job.Result = text; job.Finished = DateTime.Now;
            ParentReports.Send(key, $"tool:{label}", $"[Result of {label}, which you started in the background]\n{text}");
        });

        return $"{label} started in the background. Carry on — its result will reach you when it finishes, so don't wait for it. (Check on it or stop it with the jobs tool.)";
    }
}

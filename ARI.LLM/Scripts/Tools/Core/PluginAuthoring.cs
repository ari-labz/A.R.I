using System.Text;

namespace ARI.LLM;

// How Ari writes plugins for herself. A plugin is a folder of C# files saved under Plugins/ in the server's data folder; it compiles at once and
// appears on the Plugins page, switched off. Owner's app chats only: a plugin runs with full access to this machine.
[Tool("plugin_docs", "How to write a plugin of your own: what a plugin is, the format, settings, a background part, and a worked example. Read it before your first write_plugin.", Access = ToolAccess.Read, Group = "authoring_tools")]
public class PluginDocs : Tool
{
    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can write plugins.";

    public override Task<string> Run() => Task.FromResult("""
        A plugin is a folder of C# files: one class marked [Plugin] that inherits ScriptPlugin, plus any number of [Tool] classes (see tool_docs for how a tool is
        written). The tools are the plugin's tools: they are in your tool list only while the plugin is switched on, and they go when it is switched off.
        The plugin keeps its own settings, can run something in the background, and shows on the Plugins page with its settings and its code.

        Save each file with write_plugin (the plugin's id is the folder name, e.g. "weather"). Every save compiles the whole folder; if it doesn't compile you get
        the file, line and error back and the previous version keeps running. A new plugin starts SWITCHED OFF: the owner switches it on from the Plugins page,
        because it runs with full access to this machine. Until then you can try its tools with test_plugin.

        [Plugin("Weather", "Looks up the weather for a city.")]
        public class WeatherPlugin : ScriptPlugin
        {
            // What the owner can set on the plugin's page. The page draws the form from this.
            public override IReadOnlyList<PluginSetting> Settings { get; } =
            [
                new("home_city", "Home city", PluginSettingType.Text, "Used when no city is given.", "Paris"),
            ];

            // Optional. Start something in the background here; stop it in OnStop.
            protected override Task OnStart() { StatusText = "Ready"; return Task.CompletedTask; }
        }

        [Tool("weather", "Get the current weather for a city. Use it when asked about weather.")]
        public class Weather : Tool
        {
            [Param("City name, e.g. Paris. Leave out to use the home city.", Optional = true)]
            public string City;

            public override async Task<string> Run()
            {
                var plugin = ScriptPlugin.Get("weather");                       // the plugin this tool belongs to
                string city = string.IsNullOrWhiteSpace(City) ? plugin.Text("home_city") : City;
                return await Http.GetStringAsync($"https://wttr.in/{city}?format=3", Cancelled);
            }
        }

        Rules:
        - One [Plugin] class per folder. It needs a public constructor with no parameters, and the constructor must do nothing but set fields: it runs when the plugin is compiled, even while it is off.
        - Setting types: Text, Secret (a token or password, never shown again), Toggle, IdList (numeric IDs). Read them with Text(key), Secret(key), Flag(key), Ids(key).
        - Settings: override Settings. Buttons: override Actions (new PluginAction("key", "Label", "what it does", Confirm: true)) and handle them in OnAction(key).
        - Background work: OnStart() runs when it is switched on and OnStop() when it is switched off. Set StatusText to say what it is doing; it shows under the plugin's name.
        - A tool reaches its plugin with ScriptPlugin.Get("id"). Tool names are lowercase letters, digits and underscores and must not clash with a built-in or another plugin's tool.
        - Everything tool_docs says about Tool classes applies: [Param] fields, Run(), Http, Cancelled, DataFolder.
        - The plugin id (the folder name) is fixed once written; to rename a plugin, write it under a new id and delete the old one.
        """);
}

[Tool("write_plugin", "Write one file of a plugin of your own (or replace it) and compile the plugin. The plugin id is its folder name. Read plugin_docs first. Returns the compile errors with line numbers if it doesn't build.", Group = "authoring_tools")]
public class WritePlugin : Tool
{
    [Param("The plugin's id: lowercase letters, digits and underscores, e.g. weather.")]
    public string Plugin;

    [Param("The file name, ending in .cs, e.g. WeatherPlugin.cs.")]
    public string File;

    [Param("The whole C# source of this file.")]
    public string Code;

    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can write plugins.";

    public override async Task<string> Run()
    {
        ScriptPluginInfo info;
        try { info = await ScriptPlugins.SaveAsync(Plugin.Trim(), File.Trim(), Code); }
        catch (ArgumentException e) { return $"[Error: {e.Message}]"; }
        StringBuilder sb = new();
        foreach (ToolDiagnostic d in info.Diagnostics) sb.AppendLine($"{d.Severity} {d.Id} at line {d.Line}, column {d.Column}: {d.Message}");
        if (info.Compiled && !info.Diagnostics.Any(d => d.Severity == "Error"))
            sb.Insert(0, $"Compiled. The plugin '{info.Name}' is on the Plugins page" + (info.Enabled ? " and switched on" : ", switched off. The owner switches it on there (it runs with full access to this machine); until then you can try its tools with test_plugin") +
                         (info.Tools.Count > 0 ? $". Its tools: {string.Join(", ", info.Tools)}.\n" : ". It has no tools yet.\n"));
        else sb.Insert(0, $"It didn't compile.{(info.Compiled ? " The previous version is still running." : "")}\n");
        return sb.ToString().TrimEnd();
    }
}

[Tool("test_plugin", "Call one of a plugin's tools once with the arguments you give and see exactly what it returns. Works while the plugin is still switched off.", Group = "authoring_tools")]
public class TestPlugin : Tool
{
    [Param("The plugin's id.")]
    public string Plugin;

    [Param("The tool's name, as in its [Tool(\"name\", ...)] line.")]
    public string Tool;

    [Param("The arguments as JSON, e.g. {\"city\": \"Paris\"}. Use {} for none.", Optional = true)]
    public string Args;

    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can test plugins.";

    public override async Task<string> Run() => await ScriptPlugins.TestCallAsync(Plugin.Trim(), this.Tool.Trim(), string.IsNullOrWhiteSpace(Args) ? "{}" : Args, Cancelled);
}

[Tool("read_plugin", "Read the source of one file of a plugin you or the owner wrote, to change it. Use list_my_plugins to see them.", Access = ToolAccess.Read, Group = "authoring_tools")]
public class ReadPlugin : Tool
{
    [Param("The plugin's id.")]
    public string Plugin;

    [Param("The file name, e.g. WeatherPlugin.cs")]
    public string File;

    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can read plugin source.";

    public override Task<string> Run() => Task.FromResult(ScriptPlugins.ReadCode(Plugin.Trim(), File.Trim()) ?? $"[Error: no file {File} in a plugin called {Plugin}. Use list_my_plugins.]");
}

[Tool("list_my_plugins", "List the plugins that have been written (by you or the owner): their files, whether they compile, whether they are switched on, and their tools.", Access = ToolAccess.Read, Group = "authoring_tools")]
public class ListMyPlugins : Tool
{
    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can see plugins.";

    public override Task<string> Run()
    {
        List<ScriptPluginInfo> all = ScriptPlugins.List();
        if (all.Count == 0) return Task.FromResult("No plugins written yet. Read plugin_docs, then write one with write_plugin.");
        return Task.FromResult(string.Join("\n", all.Select(p => p.Compiled
            ? $"- {p.Id}: {p.Name} ({(p.Enabled ? "on" : "off")}; files: {string.Join(", ", p.Files)}; tools: {(p.Tools.Count == 0 ? "none" : string.Join(", ", p.Tools))})"
            : $"- {p.Id}: does not compile ({p.Diagnostics.FirstOrDefault(d => d.Severity == "Error")?.Message})")));
    }
}

[Tool("delete_plugin", "Delete a plugin you or the owner wrote: it is switched off, its tools go from your list, and its folder is removed.", Group = "authoring_tools")]
public class DeletePlugin : Tool
{
    [Param("The plugin's id.")]
    public string Plugin;

    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can delete plugins.";

    public override async Task<string> Run() => await ScriptPlugins.DeleteAsync(Plugin.Trim()) ? $"Deleted {Plugin}." : $"[Error: no plugin called {Plugin}.]";
}

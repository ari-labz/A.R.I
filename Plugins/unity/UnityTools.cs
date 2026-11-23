[Tool("unity_install_bridge", "Put ARI's bridge script into a Unity project (Assets/Editor/AriBridge.cs) so you can work in its editor with the unity tool. Unity picks it up the next time it compiles. Ask before installing into someone's project.", Access = ToolAccess.ReadWrite)]
public class UnityInstallBridge : Tool
{
    [Param("The Unity project folder (the one with Assets and ProjectSettings in it), relative to the working folder or absolute. Leave out to use the working folder.", Optional = true)]
    public string Project;

    public override Task<string> Run()
    {
        string root = string.IsNullOrWhiteSpace(Project) ? WorkingFolder : System.IO.Path.IsPathRooted(Project) ? Project : System.IO.Path.Combine(WorkingFolder, Project);
        if (!Directory.Exists(System.IO.Path.Combine(root, "Assets")) || !Directory.Exists(System.IO.Path.Combine(root, "ProjectSettings")))
            throw new ToolArgException($"{root} isn't a Unity project: it needs Assets and ProjectSettings folders.");
        string target = System.IO.Path.Combine(root, "Assets", "Editor", "AriBridge.cs");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        bool had = File.Exists(target);
        File.WriteAllText(target, AriBridgeSource.Code.Replace("__PORT__", UnityBridge.Port(ScriptPlugin.Get("unity")).ToString()));
        return Task.FromResult($"{(had ? "Updated" : "Installed")} the bridge at Assets/Editor/AriBridge.cs. Once Unity has compiled it (switch to the editor if it's open), the unity tool can reach the open scene. Deleting that file disconnects it.");
    }
}

[Tool("unity", "Work in the Unity editor that has the bridge installed. Actions: status; hierarchy (the open scene's objects and their components); inspect (path: every field of every component on one object); " +
               "set (path, component, field, value: change one field, the same as editing it in the Inspector, and it can be undone in Unity); select (path: select and frame it in the Scene view); " +
               "play, stop, pause; save (the open scenes); refresh (reimport changed assets and recompile scripts); menu (item, e.g. GameObject/Create Empty); console (recent log lines, errors with their stack); " +
               "capture (view: game or scene; saves a picture into the working folder, open it with read_file to see it). Paths look like Player/Camera. Look before you change, and tell the person what you changed.", Access = ToolAccess.ReadWrite)]
public class UnityTool : Tool
{
    [Param("status, hierarchy, inspect, set, select, play, stop, pause, save, refresh, menu, console, clear_console or capture.")]
    public string Action;

    [Param("The object's path in the scene, e.g. Player/Camera (inspect, set, select).", Optional = true)]
    public string Path;

    [Param("For set: the component's type name, e.g. Transform, Light, PlayerController.", Optional = true)]
    public string Component;

    [Param("For set: the field as inspect shows it, e.g. m_LocalPosition, m_Intensity, speed.", Optional = true)]
    public string Field;

    [Param("For set: the new value. Numbers, true/false, text, an enum's name, a vector as 1,2,3, a rotation as euler angles 0,90,0, a colour as #FF8800.", Optional = true)]
    public string Value;

    [Param("For menu: the menu item's full path.", Optional = true)]
    public string Item;

    [Param("For capture: game (default) or scene.", Optional = true)]
    public string View;

    [Param("For hierarchy: how many levels deep to list (default 4). For console: how many lines (default 40).", Optional = true)]
    public int? Count;

    public override Task<string> Run()
    {
        var plugin = ScriptPlugin.Get("unity");
        string action = (Action ?? "").Trim().ToLowerInvariant();
        var args = new Dictionary<string, string>
        {
            ["path"] = Path, ["component"] = Component, ["field"] = Field, ["value"] = Value, ["item"] = Item, ["view"] = View,
            ["depth"] = action == "hierarchy" ? Count?.ToString() : null, ["lines"] = action == "console" ? Count?.ToString() : null,
        };
        if (action == "capture")
        {
            string folder = System.IO.Path.Combine(WorkingFolder, "unity_captures");
            args["file"] = System.IO.Path.Combine(folder, $"{(View == "scene" ? "scene" : "game")}_{DateTime.Now:HHmmss}.png");
            string said = UnityBridge.Ask(plugin, action, args);
            return Task.FromResult(said.StartsWith("Error") ? said : said + $" Open unity_captures/{System.IO.Path.GetFileName(args["file"])} with read_file to see it.");
        }
        return Task.FromResult(UnityBridge.Ask(plugin, action, args));
    }
}

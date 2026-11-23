[Plugin("unity", "Lets Ari work in the Unity editor with you: see the open scene, inspect and change objects, play and stop, read the console and look through a camera.")]
public class UnityPlugin : ScriptPlugin
{
    public override IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new("port", "Bridge port", PluginSettingType.Text, "The port the bridge inside Unity listens on (localhost only). Change it before installing the bridge if 7781 is taken.", "7781"),
    ];

    protected override Task OnStart()
    {
        StatusText = UnityBridge.Ask(this, "status", new()) is { } s && !s.StartsWith("Error") ? "Connected: " + s : "Waiting for Unity. Install the bridge into a project with unity_install_bridge and open it in Unity.";
        return Task.CompletedTask;
    }
}

/// <summary>Talks to the AriBridge script running inside the Unity editor.</summary>
public static class UnityBridge
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    public static int Port(ScriptPlugin plugin) => int.TryParse(plugin.Text("port"), out int p) && p is > 1024 and < 65536 ? p : 7781;

    public static string Ask(ScriptPlugin plugin, string action, Dictionary<string, string> args)
    {
        string query = string.Join("&", args.Where(a => a.Value is not null).Select(a => $"{a.Key}={Uri.EscapeDataString(a.Value)}"));
        try { return Http.GetStringAsync($"http://127.0.0.1:{Port(plugin)}/{action}?{query}").GetAwaiter().GetResult(); }
        catch (HttpRequestException) { return "Error: Unity isn't answering. Is the project open in the Unity editor with the bridge installed (unity_install_bridge)? If Unity is compiling, try again in a moment."; }
        catch (TaskCanceledException) { return "Error: Unity didn't answer in time."; }
    }
}

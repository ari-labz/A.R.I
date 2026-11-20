[Plugin("SearXNG", "Sends Ari's web searches to a SearXNG you already run (on this machine or another), instead of the one ARI starts itself.")]
public class SearxngPlugin : ScriptPlugin
{
    public override IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new("url", "SearXNG address", PluginSettingType.Text, "The address of your SearXNG, e.g. http://192.168.1.50:8080. JSON output must be switched on in its settings (formats: json).", "http://localhost:8080"),
    ];

    protected override async Task OnStart()
    {
        string url = Text("url").Trim().TrimEnd('/');
        if (!(Uri.TryCreate(url, UriKind.Absolute, out Uri u) && (u.Scheme == "http" || u.Scheme == "https")))
            throw new Exception("Set the SearXNG address first, starting with http:// or https://.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        string probe = await http.GetStringAsync(url + "/search?q=test&format=json");
        if (!probe.TrimStart().StartsWith("{")) throw new Exception("That SearXNG didn't answer with JSON. Switch on the json format in its settings.");
        WebSearchEndpoint.Url = url;
        StatusText = "Searching through " + url;
    }

    protected override Task OnStop()
    {
        WebSearchEndpoint.Url = null;
        return Task.CompletedTask;
    }
}

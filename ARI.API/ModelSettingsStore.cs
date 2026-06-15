using System.Text.Json;

namespace ARI.API;

/// <summary>Persists per-server model preferences (startup model overrides) separately from AriConfig.json.</summary>
public class ModelSettingsStore
{
    private record Settings(Dictionary<string, string> Servers = null!)
    {
        public Dictionary<string, string> Servers { get; init; } = Servers ?? new();
    }

    private readonly string filePath;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public ModelSettingsStore()
    {
        filePath = Path.Combine(AppContext.BaseDirectory, "model-settings.json");
    }

    public string GetStartupFile(string serverName)
    {
        lock (_lock)
        {
            if (!File.Exists(filePath)) return "";
            try
            {
                Settings? s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(filePath));
                return s?.Servers.TryGetValue(serverName, out string? file) == true ? file ?? "" : "";
            }
            catch { return ""; }
        }
    }

    public void SetStartupFile(string serverName, string relativeFile)
    {
        lock (_lock)
        {
            Settings current = new();
            if (File.Exists(filePath))
            {
                try { current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(filePath)) ?? new(); }
                catch { /* start fresh */ }
            }
            Dictionary<string, string> updated = new(current.Servers) { [serverName] = relativeFile };
            File.WriteAllText(filePath, JsonSerializer.Serialize(new Settings(updated), JsonOpts));
        }
    }
}

using System.Text.Json;

namespace ARI.API;

/// <summary>Persists model preferences (startup model) separately from AriConfig.json.</summary>
public class ModelSettingsStore
{
    private record Settings(string StartupFile = "");

    private readonly string filePath;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public ModelSettingsStore()
    {
        filePath = Path.Combine(AppContext.BaseDirectory, "model-settings.json");
    }

    public string GetStartupFile()
    {
        lock (_lock)
        {
            if (!File.Exists(filePath)) return "";
            try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(filePath))?.StartupFile ?? ""; }
            catch { return ""; }
        }
    }

    public void SetStartupFile(string relativeFile)
    {
        lock (_lock)
        {
            File.WriteAllText(filePath, JsonSerializer.Serialize(new Settings(relativeFile), JsonOpts));
        }
    }
}

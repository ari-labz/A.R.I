using System.Text.Json;

namespace ARI.API;

public class ModelNotesStore
{
    private readonly string filePath;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public ModelNotesStore()
    {
        filePath = Path.Combine(AppContext.BaseDirectory, "model-notes.json");
    }

    public Dictionary<string, string> GetAll()
    {
        lock (_lock)
        {
            if (!File.Exists(filePath)) return new();
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(filePath)) ?? new(); }
            catch { return new(); }
        }
    }

    public string Get(string modelFile)
    {
        var all = GetAll();
        return all.TryGetValue(modelFile, out string? note) ? note : "";
    }

    public void Set(string modelFile, string notes)
    {
        lock (_lock)
        {
            var all = GetAll();
            if (string.IsNullOrWhiteSpace(notes))
                all.Remove(modelFile);
            else
                all[modelFile] = notes;
            File.WriteAllText(filePath, JsonSerializer.Serialize(all, JsonOpts));
        }
    }
}

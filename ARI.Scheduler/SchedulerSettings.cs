using System.Text.Json;

namespace ARI.Scheduler;

/// <summary>
/// Runtime-editable scheduler overrides, persisted to Scheduler.Settings.json under PersistentData.
/// AriConfig.json is read-only at startup, so control-panel edits (cron per task, active hours, the
/// proactive on/off switch) live here instead and are layered over the AriConfig defaults on load.
/// A null/absent field means "fall back to the AriConfig default".
/// </summary>
public sealed class SchedulerSettings
{
    public Dictionary<string, string> Schedules { get; set; } = new();
    public int?  QuietStartHour   { get; set; }
    public int?  QuietEndHour     { get; set; }
    public bool? ProactiveEnabled { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static SchedulerSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<SchedulerSettings>(File.ReadAllText(path), JsonOpts) ?? new();
        }
        catch { /* fall through to defaults */ }
        return new();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }
}

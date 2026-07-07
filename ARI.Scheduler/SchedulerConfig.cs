using System.Text.Json.Serialization;

namespace ARI.Scheduler;

/// <summary>Config for the scheduler module (lives under Modules.Scheduler in AriConfig).</summary>
public class SchedulerConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>How often the scheduler wakes to check for due tasks and idle windows, in seconds.</summary>
    [JsonPropertyName("TickSeconds")]
    public int TickSeconds { get; init; } = 30;

    /// <summary>Cron schedule per task name; overrides a task's built-in default when present.</summary>
    [JsonPropertyName("Schedules")]
    public Dictionary<string, string> Schedules { get; init; } = new();

    /// <summary>Hour (local, 0-23) proactive messages stop. Default 22 (10pm).</summary>
    [JsonPropertyName("QuietStartHour")]
    public int QuietStartHour { get; init; } = 22;

    /// <summary>Hour (local, 0-23) proactive messages resume. Default 8 (8am).</summary>
    [JsonPropertyName("QuietEndHour")]
    public int QuietEndHour { get; init; } = 8;

    /// <summary>True if the given local hour falls in the quiet window (handles the overnight wrap).</summary>
    public bool IsQuietHour(int hour) =>
        QuietStartHour <= QuietEndHour
            ? hour >= QuietStartHour && hour < QuietEndHour
            : hour >= QuietStartHour || hour < QuietEndHour;
}

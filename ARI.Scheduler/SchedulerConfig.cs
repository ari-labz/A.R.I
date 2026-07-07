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
}

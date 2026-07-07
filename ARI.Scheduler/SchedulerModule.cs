using System.Text.Json;
using ARI.LLM;
using Microsoft.Extensions.Logging;

namespace ARI.Scheduler;

/// <summary>
/// Runs cron-scheduled background jobs, but ONLY while Ari is idle — so nothing here ever competes
/// with a live response. A job due while Ari is busy stays due and runs the moment she goes idle.
/// A running job is cancelled the instant Ari becomes busy (the handler gets a tripped token); since
/// a cancelled job is not marked complete, it stays due and resumes on the next idle window.
///
/// Register jobs with AddTask before Start. Cron schedules come from the task's default or a
/// SchedulerConfig override. Last-run times persist to Scheduler.json so overdue survives restarts.
/// </summary>
public sealed class SchedulerModule : IDisposable
{
    private readonly SchedulerConfig _config;
    private readonly ILogger _logger;
    private readonly string _statePath;
    private readonly List<ScheduledTask> _tasks = new();
    private readonly Dictionary<string, DateTime> _lastRun;

    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    // How often, while a job runs, we re-check that Ari is still idle. Small so we yield promptly.
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(1);

    public SchedulerModule(SchedulerConfig config, string persistentDataDir, ILogger logger)
    {
        _config = config;
        _logger = logger;
        _statePath = Path.Combine(persistentDataDir, "Scheduler.json");
        _lastRun = LoadState();
    }

    /// <summary>Register a job. <paramref name="defaultCron"/> is used unless overridden in config.</summary>
    public void AddTask(string name, string defaultCron, Func<CancellationToken, Task> handler)
    {
        string cron = _config.Schedules.TryGetValue(name, out string? c) && !string.IsNullOrWhiteSpace(c) ? c : defaultCron;
        DateTime lastRun = _lastRun.TryGetValue(name, out DateTime lr) ? lr : DateTime.UtcNow;
        _tasks.Add(new ScheduledTask(name, cron, handler, lastRun));
        _logger.LogInformation("[Scheduler] Registered task '{Name}' (cron: {Cron}).", name, cron);
    }

    public void Start()
    {
        if (!_config.Enabled) { _logger.LogInformation("[Scheduler] Disabled."); return; }
        if (_tasks.Count == 0) { _logger.LogInformation("[Scheduler] No tasks registered — not starting."); return; }
        _loopCts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoop(_loopCts.Token));
        _logger.LogInformation("[Scheduler] Started with {Count} task(s), tick {Tick}s.", _tasks.Count, _config.TickSeconds);
    }

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Only touch background work when nothing is live.
                if (Activity.IsIdle())
                {
                    foreach (ScheduledTask task in _tasks)
                    {
                        if (ct.IsCancellationRequested || !Activity.IsIdle()) break;
                        if (task.IsDue(DateTime.UtcNow))
                            await RunTask(task, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Scheduler] Loop error.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _config.TickSeconds)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunTask(ScheduledTask task, CancellationToken loopCt)
    {
        _logger.LogInformation("[Scheduler] Running '{Name}'...", task.Name);

        // This token trips when Ari becomes busy OR the module shuts down — the handler yields on it.
        using CancellationTokenSource jobCts = CancellationTokenSource.CreateLinkedTokenSource(loopCt);

        // Watchdog: cancel the job the moment Ari is no longer idle.
        Task watchdog = Task.Run(async () =>
        {
            while (!jobCts.IsCancellationRequested)
            {
                if (!Activity.IsIdle()) { jobCts.Cancel(); break; }
                try { await Task.Delay(IdlePoll, jobCts.Token); } catch { break; }
            }
        });

        try
        {
            await task.Handler(jobCts.Token);
            // Completed fully — mark run so the next occurrence is computed from now.
            task.LastRunUtc = DateTime.UtcNow;
            _lastRun[task.Name] = task.LastRunUtc;
            SaveState();
            _logger.LogInformation("[Scheduler] '{Name}' complete.", task.Name);
        }
        catch (OperationCanceledException)
        {
            // Interrupted by activity/shutdown — NOT marked complete, so it stays due and resumes.
            _logger.LogInformation("[Scheduler] '{Name}' yielded (Ari became busy); will resume when idle.", task.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Scheduler] '{Name}' failed.", task.Name);
        }
        finally
        {
            jobCts.Cancel();
            try { await watchdog; } catch { }
        }
    }

    // ── State persistence ─────────────────────────────────────────────────────────────

    private Dictionary<string, DateTime> LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
                return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_statePath)) ?? new();
        }
        catch (Exception ex) { _logger.LogWarning("[Scheduler] Could not read state: {Msg}", ex.Message); }
        return new();
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(_lastRun, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _logger.LogWarning("[Scheduler] Could not write state: {Msg}", ex.Message); }
    }

    public void Dispose()
    {
        _loopCts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _loopCts?.Dispose();
    }
}

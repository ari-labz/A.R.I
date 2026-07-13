using System.Text.Json;
using ARI.Common;
using ARI.LLM;
using Cronos;
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
public sealed class SchedulerModule : IDisposable, ISchedulerModule
{
    private readonly SchedulerConfig _config;
    private readonly ILogger _logger;
    private readonly string _statePath;
    private readonly string _settingsPath;
    private readonly SchedulerSettings _settings;
    private readonly object _settingsLock = new();
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
        _settingsPath = Path.Combine(persistentDataDir, "Scheduler.Settings.json");
        _settings = SchedulerSettings.Load(_settingsPath);
        _lastRun = LoadState();
    }

    /// <summary>Register a job. Cron precedence: runtime override (control panel) &gt; AriConfig override
    /// &gt; the task's built-in default.</summary>
    public void AddTask(string name, string defaultCron, Func<CancellationToken, Task> handler, bool uninterruptible = false)
    {
        string cron =
            _settings.Schedules.TryGetValue(name, out string? s) && IsValidCron(s) ? s
          : _config.Schedules.TryGetValue(name, out string? c) && !string.IsNullOrWhiteSpace(c) ? c
          : defaultCron;
        DateTime lastRun = _lastRun.TryGetValue(name, out DateTime lr) ? lr : DateTime.UtcNow;
        _tasks.Add(new ScheduledTask(name, cron, handler, lastRun, uninterruptible));
        _logger.LogInformation("[Scheduler] Registered task '{Name}' (cron: {Cron}).", name, cron);
    }

    // ── ISchedulerModule (control-panel surface) ──────────────────────────────────────

    public bool Enabled => _config.Enabled;

    public IReadOnlyList<SchedulerTaskInfo> GetTasks()
    {
        lock (_settingsLock)
            return _tasks.Select(t => new SchedulerTaskInfo(
                t.Name, t.CronText,
                _lastRun.TryGetValue(t.Name, out DateTime lr) && lr != default ? lr : (DateTime?)null,
                t.NextRunUtc())).ToList();
    }

    public bool SetTaskCron(string name, string cron)
    {
        if (!IsValidCron(cron)) return false;
        lock (_settingsLock)
        {
            ScheduledTask? task = _tasks.FirstOrDefault(t => t.Name == name);
            if (task is null) return false;
            task.UpdateCron(cron.Trim());
            _settings.Schedules[name] = cron.Trim();
            _settings.Save(_settingsPath);
        }
        _logger.LogInformation("[Scheduler] Task '{Name}' cron updated to '{Cron}'.", name, cron.Trim());
        return true;
    }

    public bool ProactiveEnabled
    {
        get { lock (_settingsLock) return _settings.ProactiveEnabled ?? true; }
        set
        {
            lock (_settingsLock) { _settings.ProactiveEnabled = value; _settings.Save(_settingsPath); }
            _logger.LogInformation("[Scheduler] Proactive messages {State}.", value ? "enabled" : "disabled");
        }
    }

    public (int QuietStartHour, int QuietEndHour) QuietHours
    {
        get { lock (_settingsLock) return (_settings.QuietStartHour ?? _config.QuietStartHour, _settings.QuietEndHour ?? _config.QuietEndHour); }
    }

    public void SetQuietHours(int quietStartHour, int quietEndHour)
    {
        int start = ((quietStartHour % 24) + 24) % 24;
        int end   = ((quietEndHour   % 24) + 24) % 24;
        lock (_settingsLock) { _settings.QuietStartHour = start; _settings.QuietEndHour = end; _settings.Save(_settingsPath); }
        _logger.LogInformation("[Scheduler] Quiet hours set to {Start}:00–{End}:00.", start, end);
    }

    public bool IsQuietHour(int hour)
    {
        (int start, int end) = QuietHours;
        return start <= end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    private static bool IsValidCron(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return false;
        try { CronExpression.Parse(cron.Trim()); return true; }
        catch { return false; }
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

        // Watchdog: cancel the job the moment Ari is no longer idle. Uninterruptible tasks skip it —
        // they still only START in an idle window (the loop gates on IsIdle), but once running they
        // finish even if Ari becomes active again. The proactive messenger needs this: its own draft
        // thread marks Ari busy, so a watchdog would cancel the very message it is generating.
        Task watchdog = task.Uninterruptible ? Task.CompletedTask : Task.Run(async () =>
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

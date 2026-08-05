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
/// SchedulerConfig override. Last-run times persist to Scheduler.json; slots are only owed from the
/// point the server came up, so a schedule missed while Ari was off is skipped rather than caught up.
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

    // Slots that fell before this are never owed — that is what stops a boot from firing everything
    // that passed while Ari was off.
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    // The job in flight, if any, and the token that stops it (shutdown or the control panel).
    private readonly object _runLock = new();
    private string? _runningTask;
    private CancellationTokenSource? _jobCts;

    /// <summary>Fired when a scheduled task starts or stops. Args: (taskName, running).</summary>
    public event Action<string, bool>? TaskStateChanged;

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
    public void AddTask(string name, string defaultCron, Func<CancellationToken, Task> handler, bool respectActivity = false)
    {
        string cron =
            _settings.Schedules.TryGetValue(name, out string? s) && IsValidCron(s) ? s
          : _config.Schedules.TryGetValue(name, out string? c) && !string.IsNullOrWhiteSpace(c) ? c
          : defaultCron;
        DateTime lastRun = _lastRun.TryGetValue(name, out DateTime lr) ? lr : DateTime.UtcNow;
        _tasks.Add(new ScheduledTask(name, cron, handler, lastRun, respectActivity));
        _logger.LogInformation("[Scheduler] Registered task '{Name}' (cron: {Cron}{Activity}).",
            name, cron, respectActivity ? ", activity-aware" : "");
    }

    // How long a due slot is pushed back each time Ari is busy, and how many times before the slot is
    // dropped and the task falls through to its next cron occurrence.
    private static readonly TimeSpan DeferWindow = TimeSpan.FromMinutes(30);
    private const int MaxDeferrals = 3;

    // ── ISchedulerModule (control-panel surface) ──────────────────────────────────────

    public bool Enabled => _config.Enabled;

    public IReadOnlyList<SchedulerTaskInfo> GetTasks()
    {
        string? running = RunningTask;
        lock (_settingsLock)
            return _tasks.Select(t => new SchedulerTaskInfo(
                t.Name, t.CronText,
                _lastRun.TryGetValue(t.Name, out DateTime lr) && lr != default ? lr : (DateTime?)null,
                t.NextRunUtc(_startedUtc),
                t.Name == running)).ToList();
    }

    public string? RunningTask { get { lock (_runLock) return _runningTask; } }

    /// <summary>Stops the named job if it is the one currently running. The slot is consumed — the
    /// task waits for its next scheduled time rather than resuming.</summary>
    public bool StopTask(string name)
    {
        lock (_runLock)
        {
            if (_runningTask != name || _jobCts is null) return false;
            _jobCts.Cancel();
        }
        _logger.LogInformation("[Scheduler] Stop requested for '{Name}'.", name);
        return true;
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
                // Most background jobs run whether or not Ari is busy — they share the llama server with
                // live conversation. Activity-aware jobs (RespectActivity) instead defer their slot while
                // Ari is in conversation; see ClearedByActivityGate.
                foreach (ScheduledTask task in _tasks)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!task.IsDue(DateTime.UtcNow, _startedUtc)) continue;
                    if (task.RespectActivity && !ClearedByActivityGate(task)) continue;
                    await RunTask(task, ct);
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

    // Returns true if an activity-aware task's due slot may run now. While Ari is in conversation the
    // slot is pushed back by DeferWindow; after MaxDeferrals pushes it is dropped (the slot is consumed)
    // and the task falls through to its next cron occurrence. The deferral state resets whenever the
    // slot clears — either because Ari went idle or because it was dropped.
    private bool ClearedByActivityGate(ScheduledTask task)
    {
        DateTime now = DateTime.UtcNow;

        // Still waiting out a previous deferral — leave it until the re-check time.
        if (task.DeferredUntil is DateTime until && now < until) return false;

        if (!(Modules.Llm?.ConversationActive ?? false))
        {
            task.DeferredUntil = null;
            task.DeferCount = 0;
            return true;   // Ari is idle — clear to run.
        }

        task.DeferCount++;
        if (task.DeferCount >= MaxDeferrals)
        {
            _logger.LogInformation("[Scheduler] '{Name}' deferred {Count}× while Ari was in conversation — dropping this slot; waiting for its next scheduled time.",
                task.Name, task.DeferCount);
            task.DeferredUntil = null;
            task.DeferCount = 0;
            MarkRun(task);   // consume the slot so the next occurrence is measured from now
            return false;
        }

        task.DeferredUntil = now + DeferWindow;
        _logger.LogInformation("[Scheduler] '{Name}' deferred {Count}/{Max} — Ari in conversation; re-checking in {Minutes} min.",
            task.Name, task.DeferCount, MaxDeferrals, (int)DeferWindow.TotalMinutes);
        return false;
    }

    private async Task RunTask(ScheduledTask task, CancellationToken loopCt)
    {
        _logger.LogInformation("[Scheduler] Running '{Name}'...", task.Name);

        // Trips on shutdown or a Stop from the control panel — the handler yields on it.
        using CancellationTokenSource jobCts = CancellationTokenSource.CreateLinkedTokenSource(loopCt);
        lock (_runLock) { _runningTask = task.Name; _jobCts = jobCts; }
        TaskStateChanged?.Invoke(task.Name, true);

        try
        {
            await task.Handler(jobCts.Token);
            _logger.LogInformation("[Scheduler] '{Name}' complete.", task.Name);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Scheduler] '{Name}' stopped; waiting for its next scheduled time.", task.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Scheduler] '{Name}' failed.", task.Name);
        }
        finally
        {
            // The slot is consumed however the run ended — completed, stopped or failed. Anything
            // else leaves the task due and, with no idle gate to hold it back, retrying every tick.
            MarkRun(task);
            lock (_runLock) { _runningTask = null; _jobCts = null; }
            TaskStateChanged?.Invoke(task.Name, false);
        }
    }

    // Marks the slot as consumed so the next occurrence is measured from now.
    private void MarkRun(ScheduledTask task)
    {
        task.LastRunUtc = DateTime.UtcNow;
        _lastRun[task.Name] = task.LastRunUtc;
        SaveState();
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

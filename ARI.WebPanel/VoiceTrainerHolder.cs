using ARI.VoiceSynthesis;

namespace ARI.WebPanel;

/// <summary>
/// Singleton that manages the currently active (or last completed) training job.
/// Only one job runs at a time.
/// </summary>
public class VoiceTrainerHolder
{
    private readonly object _lock = new();
    private TrainingJob? _job;

    public TrainingJob? Current
    {
        get { lock (_lock) return _job; }
    }

    /// <summary>Starts a new training job. Throws if one is already running.</summary>
    public TrainingJob Start(VoiceTrainer trainer, string modelName, CancellationToken appStopping)
    {
        lock (_lock)
        {
            if (_job?.IsRunning == true)
                throw new InvalidOperationException("A training job is already running.");

            var job = new TrainingJob(modelName, appStopping);
            _job = job;
            job.Run(trainer);
            return job;
        }
    }
}

public class TrainingJob
{
    private readonly List<TrainingProgressEvent> _events = new();
    private readonly CancellationTokenSource _cts;

    public string JobId    { get; } = Guid.NewGuid().ToString("N")[..8];
    public string ModelName { get; }
    public bool   IsRunning { get; private set; } = true;
    public bool   IsSuccess { get; private set; }
    public string? Error    { get; private set; }
    public string? PthPath  { get; private set; }
    public string? IndexPath { get; private set; }

    public IReadOnlyList<TrainingProgressEvent> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    internal TrainingJob(string modelName, CancellationToken appStopping)
    {
        ModelName = modelName;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(appStopping);
    }

    internal void Run(VoiceTrainer trainer)
    {
        var progress = new Progress<TrainingProgress>(p =>
        {
            var ev = new TrainingProgressEvent(p.Step, p.Percent, p.Detail, DateTime.UtcNow);
            lock (_events) _events.Add(ev);
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var (pth, idx) = await trainer.TrainAsync(progress, _cts.Token);
                PthPath   = pth;
                IndexPath = idx;
                IsSuccess = true;
                AddEvent("Complete", 100, $"Model saved to {Path.GetFileName(pth)}");
            }
            catch (OperationCanceledException)
            {
                Error = "Training cancelled.";
                AddEvent("Cancelled", 0, null);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                AddEvent("Error", 0, ex.Message);
            }
            finally
            {
                IsRunning = false;
            }
        }, _cts.Token);
    }

    public void Cancel() => _cts.Cancel();

    private void AddEvent(string step, int pct, string? detail)
    {
        var ev = new TrainingProgressEvent(step, pct, detail, DateTime.UtcNow);
        lock (_events) _events.Add(ev);
    }
}

public record TrainingProgressEvent(string Step, int Percent, string? Detail, DateTime Timestamp);

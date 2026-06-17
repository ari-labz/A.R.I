namespace ARI.VoiceSynthesis;

/// <summary>Manages the active voice training job. Always registered; reports not ready if setup is incomplete.</summary>
public class VoiceSynthesisModule
{
    private readonly object _lock = new();
    private TrainingJob? _current;

    public bool IsSetupComplete { get; private set; }
    public void MarkSetupComplete() => IsSetupComplete = true;

    public TrainingJob? Current
    {
        get { lock (_lock) return _current; }
    }

    public TrainingJob Start(StyleTtsTrainer trainer, string modelName, CancellationToken appStopping)
    {
        lock (_lock)
        {
            if (_current?.IsRunning == true)
                throw new InvalidOperationException("A training job is already running.");

            TrainingJob job = new(modelName, appStopping);
            _current = job;
            job.Run(trainer);
            return job;
        }
    }
}

public class TrainingJob
{
    private readonly List<TrainingProgressEvent> _events = new();
    private readonly CancellationTokenSource     _cts;

    public string  JobId     { get; } = Guid.NewGuid().ToString("N")[..8];
    public string  ModelName { get; }
    public bool    IsRunning { get; private set; } = true;
    public bool    IsSuccess { get; private set; }
    public string? Error     { get; private set; }
    public string? Checkpoint { get; private set; }

    public IReadOnlyList<TrainingProgressEvent> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    internal TrainingJob(string modelName, CancellationToken appStopping)
    {
        ModelName = modelName;
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(appStopping);
    }

    internal void Run(StyleTtsTrainer trainer)
    {
        Progress<TrainingProgress> progress = new(p =>
        {
            lock (_events) _events.Add(new TrainingProgressEvent(p.Step, p.Percent, p.Detail, DateTime.UtcNow));
        });

        _ = Task.Run(async () =>
        {
            try
            {
                Checkpoint = await trainer.Train(progress, _cts.Token);
                IsSuccess  = true;
                AddEvent("Complete", 100, $"Model saved to {Path.GetFileName(Checkpoint)}");
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
            finally { IsRunning = false; }
        }, _cts.Token);
    }

    public void Cancel() => _cts.Cancel();

    private void AddEvent(string step, int pct, string? detail)
    {
        lock (_events) _events.Add(new TrainingProgressEvent(step, pct, detail, DateTime.UtcNow));
    }
}

public record TrainingProgressEvent(string Step, int Percent, string? Detail, DateTime Timestamp);

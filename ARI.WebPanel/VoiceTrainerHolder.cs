using ARI.VoiceSynthesis;

namespace ARI.WebPanel;

public class VoiceTrainerHolder
{
    private readonly object lockObj = new();
    private TrainingJob? current;

    public TrainingJob? Current
    {
        get { lock (lockObj) return current; }
    }

    public TrainingJob Start(F5Trainer trainer, string modelName, CancellationToken appStopping)
    {
        lock (lockObj)
        {
            if (current?.IsRunning == true)
                throw new InvalidOperationException("A training job is already running.");

            TrainingJob job = new(modelName, appStopping);
            current = job;
            job.Run(trainer);
            return job;
        }
    }
}

public class TrainingJob
{
    private readonly List<TrainingProgressEvent> events = new();
    private readonly CancellationTokenSource cts;

    public string  JobId      { get; } = Guid.NewGuid().ToString("N")[..8];
    public string  ModelName  { get; }
    public bool    IsRunning  { get; private set; } = true;
    public bool    IsSuccess  { get; private set; }
    public string? Error      { get; private set; }
    public string? Checkpoint { get; private set; }

    public IReadOnlyList<TrainingProgressEvent> Events
    {
        get { lock (events) return events.ToList(); }
    }

    internal TrainingJob(string modelName, CancellationToken appStopping)
    {
        ModelName = modelName;
        cts = CancellationTokenSource.CreateLinkedTokenSource(appStopping);
    }

    internal void Run(F5Trainer trainer)
    {
        Progress<TrainingProgress> progress = new(p =>
        {
            TrainingProgressEvent ev = new(p.Step, p.Percent, p.Detail, DateTime.UtcNow);
            lock (events) events.Add(ev);
        });

        _ = Task.Run(async () =>
        {
            try
            {
                Checkpoint = await trainer.Train(progress, cts.Token);
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
            finally
            {
                IsRunning = false;
            }
        }, cts.Token);
    }

    public void Cancel() => cts.Cancel();

    private void AddEvent(string step, int pct, string? detail)
    {
        TrainingProgressEvent ev = new(step, pct, detail, DateTime.UtcNow);
        lock (events) events.Add(ev);
    }
}

public record TrainingProgressEvent(string Step, int Percent, string? Detail, DateTime Timestamp);

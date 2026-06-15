namespace ARI.API;

public record ModelInfo(
    string Name,
    string File,
    long   FileSizeBytes,
    bool   Configured,
    bool   IsStartup,
    bool   HasMmproj,
    bool   SupportsVision,
    bool   HasMtp,
    string DownloadUrl);

public record ModelSwitchEvent(string Phase, string Message, int Percent);

public class ModelSwitchJob
{
    public string TargetFile { get; init; } = "";
    private readonly List<ModelSwitchEvent> _events = new();
    public IReadOnlyList<ModelSwitchEvent> Events => _events;
    public bool    IsRunning { get; private set; } = true;
    public bool    IsSuccess { get; private set; }
    public string? Error     { get; private set; }

    public void AddEvent(string phase, string message, int percent)
        => _events.Add(new ModelSwitchEvent(phase, message, percent));

    public void Complete(bool success, string? error = null)
    {
        IsSuccess = success;
        Error     = error;
        IsRunning = false;
    }
}

/// <summary>
/// Singleton published by ApiService. ModelManager (ARI.Core) writes into it;
/// controllers read from it. Mirrors the LlmServiceHolder pattern.
/// </summary>
public class ModelManagerHolder
{
    public string?                  ActiveFile   { get; private set; }
    public string?                  ActiveName   { get; private set; }
    public int                      ActivePid    { get; private set; } = -1;
    public IReadOnlyList<ModelInfo> AllModels    { get; private set; } = Array.Empty<ModelInfo>();
    public ModelSwitchJob?          CurrentSwitchJob { get; private set; }

    public void Initialize(IReadOnlyList<ModelInfo> models)
        => AllModels = models;

    public void SetActiveModel(string? file, string? name, int pid)
    {
        ActiveFile = file;
        ActiveName = name;
        ActivePid  = pid;
    }

    public ModelSwitchJob BeginSwitchJob(string targetFile)
    {
        var job = new ModelSwitchJob { TargetFile = targetFile };
        CurrentSwitchJob = job;
        return job;
    }

    private Action<string>? _switchDelegate;
    public void RegisterSwitchDelegate(Action<string> action) => _switchDelegate = action;
    public void TriggerSwitch(string relativeFile) => _switchDelegate?.Invoke(relativeFile);
}

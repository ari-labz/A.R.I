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

public record ServerStatus(string? ActiveFile, string? ActiveName, int Pid, int ContextSize = 0);

public class ModelSwitchJob
{
    public string TargetFile  { get; init; } = "";
    public string ServerName  { get; init; } = "";
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
/// controllers read from it.
/// </summary>
public class ModelManagerHolder
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ServerStatus> _servers = new();
    public IReadOnlyDictionary<string, ServerStatus> Servers => _servers;

    public IReadOnlyList<ModelInfo> AllModels { get; private set; } = Array.Empty<ModelInfo>();
    public ModelSwitchJob?          CurrentSwitchJob { get; private set; }

    public void Initialize(IReadOnlyList<ModelInfo> models)
        => AllModels = models;

    public void SetServerModel(string serverName, string? file, string? name, int pid, int contextSize = 0)
        => _servers[serverName] = new ServerStatus(file, name, pid, contextSize);

    // ── Legacy single-server accessors (used by older API paths) ──────────────
    public string? ActiveFile => _servers.Values.FirstOrDefault()?.ActiveFile;
    public string? ActiveName => _servers.Values.FirstOrDefault()?.ActiveName;
    public int     ActivePid  => _servers.Values.FirstOrDefault()?.Pid ?? -1;

    public ModelSwitchJob BeginSwitchJob(string serverName, string targetFile)
    {
        var job = new ModelSwitchJob { TargetFile = targetFile, ServerName = serverName };
        CurrentSwitchJob = job;
        return job;
    }

    private Action<string, string>? _switchDelegate;
    public void RegisterSwitchDelegate(Action<string, string> action) => _switchDelegate = action;
    public void TriggerSwitch(string serverName, string relativeFile) => _switchDelegate?.Invoke(serverName, relativeFile);

    private Func<Task>? _stopAllDelegate;
    private Func<Task>? _restartAllDelegate;
    public void RegisterPauseResumeDelegate(Func<Task> stopAll, Func<Task> restartAll)
    {
        _stopAllDelegate    = stopAll;
        _restartAllDelegate = restartAll;
    }
    public Task StopAllServersAsync()    => _stopAllDelegate?.Invoke()    ?? Task.CompletedTask;
    public Task RestartAllServersAsync() => _restartAllDelegate?.Invoke() ?? Task.CompletedTask;
}

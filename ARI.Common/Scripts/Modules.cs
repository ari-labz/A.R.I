namespace ARI.Common;

public interface IDiscordModule
{
    Task NotifyOwner(string message);
    Task NotifyOffline();

    /// <summary>Deletes every message ARI sent within the given window, across all reachable
    /// channels (guild text channels and the owner DM). Returns the number of messages deleted.</summary>
    Task<int> DeleteRecentMessagesAsync(TimeSpan window);
}

public interface ILLMModule
{
    Task StopAllServersAsync();
    Task RestartAllServersAsync();
    bool AssignAgentServer(string agentName, string serverName);
    bool AssignAgentSlot(string agentName, int? slot);
    /// <summary>True when no thread is currently being processed — Ari is idle.</summary>
    bool IsIdle { get; }
}

public interface IVoiceModule
{
    bool    IsReady     { get; }
    string? ActiveModel { get; }
    Task<byte[]> Synthesise(string text, CancellationToken ct, int diffusionSteps = 5, float alpha = 0.3f, float beta = 0.7f, float embeddingScale = 1.0f, float? speed = null, float? pauseScale = null);
    Task<byte[]> SynthesiseWithCheckpoint(string text, string checkpointPath, CancellationToken ct, int diffusionSteps = 5, float alpha = 0.3f, float beta = 0.7f, float embeddingScale = 1.0f, float? speed = null, float? pauseScale = null);
    (float speed, float pauseScale) GetVoiceSettings();
    void SetVoiceSettings(float speed, float pauseScale);
    /// <summary>Queue text to be spoken with the currently-selected voice (host playback).</summary>
    void Speak(string text);
}

public interface IVoiceSynthesisModule
{
    bool IsSetupComplete { get; }
}

public interface IBrainModule { }

/// <summary>Live view of one scheduled job for the control panel.</summary>
public record SchedulerTaskInfo(string Name, string Cron, DateTime? LastRunUtc, DateTime? NextRunUtc, bool Running);

public interface ISchedulerModule
{
    /// <summary>Whether the scheduler loop itself is running.</summary>
    bool Enabled { get; }

    /// <summary>Current jobs with their cron, last-run and next-run times.</summary>
    IReadOnlyList<SchedulerTaskInfo> GetTasks();

    /// <summary>Name of the job currently running, or null when nothing is in flight.</summary>
    string? RunningTask { get; }

    /// <summary>Stops the named job if it is the one running. The slot is consumed — the task waits
    /// for its next scheduled time rather than resuming. False if it is not currently running.</summary>
    bool StopTask(string name);

    /// <summary>Updates a job's cron expression live and persists it. Returns false if the cron is invalid
    /// or the task is unknown.</summary>
    bool SetTaskCron(string name, string cron);

    /// <summary>Master switch for proactive messages (checked by the ProactiveMessage job at fire time).</summary>
    bool ProactiveEnabled { get; set; }

    /// <summary>Active-hours window: proactive messages are held during the quiet hours OUTSIDE this window.
    /// Stored as the quiet-window bounds (start = when they stop, end = when they resume), local 0-23.</summary>
    (int QuietStartHour, int QuietEndHour) QuietHours { get; }

    /// <summary>Sets the quiet-hours window (local 0-23) and persists it.</summary>
    void SetQuietHours(int quietStartHour, int quietEndHour);

    /// <summary>True if the given local hour falls in the quiet window.</summary>
    bool IsQuietHour(int hour);
}

public interface IWebPushModule
{
    /// <summary>VAPID public key (base64url) the browser needs to create a push subscription.</summary>
    string VapidPublicKey { get; }

    /// <summary>Stores (or refreshes) a browser push subscription so it receives future notifications.</summary>
    void AddSubscription(string endpoint, string p256dh, string auth);

    /// <summary>Removes a push subscription (e.g. on unsubscribe or when the endpoint is gone).</summary>
    void RemoveSubscription(string endpoint);

    /// <summary>Sends a push notification to every registered device. <paramref name="url"/> deep-links the
    /// notification click (e.g. a thread); <paramref name="title"/> overrides the default notification title.</summary>
    Task SendPushNotification(string text, string? url = null, string? title = null);
}

public interface IListenerModule
{
    bool IsReady { get; }
}

public static class Modules
{
    public static IDiscordModule?        Discord        { get; private set; }
    public static ILLMModule?            Llm            { get; private set; }
    public static IVoiceModule?          Voice          { get; private set; }
    public static IVoiceSynthesisModule? VoiceSynthesis { get; private set; }
    public static IBrainModule?          Brain          { get; private set; }
    public static IListenerModule?       Listener       { get; private set; }
    public static IWebPushModule?        WebPush        { get; private set; }
    public static ISchedulerModule?      Scheduler      { get; private set; }
    public static IProjectService?       Projects       { get; private set; }

    public static void Register(
        IDiscordModule?        discord        = null,
        ILLMModule?            llm            = null,
        IVoiceModule?          voice          = null,
        IVoiceSynthesisModule? voiceSynthesis = null,
        IBrainModule?          brain          = null,
        IListenerModule?       listener       = null,
        IWebPushModule?        webPush        = null,
        ISchedulerModule?      scheduler      = null,
        IProjectService?       projects       = null)
    {
        if (discord        is not null) Discord        = discord;
        if (llm            is not null) Llm            = llm;
        if (voice          is not null) Voice          = voice;
        if (voiceSynthesis is not null) VoiceSynthesis = voiceSynthesis;
        if (brain          is not null) Brain          = brain;
        if (listener       is not null) Listener       = listener;
        if (webPush        is not null) WebPush        = webPush;
        if (scheduler      is not null) Scheduler      = scheduler;
        if (projects       is not null) Projects       = projects;
    }
}

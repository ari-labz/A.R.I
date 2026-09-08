namespace ARI.Common;

public record VoiceChannelInfo(ulong ChannelId, string ChannelName, ulong GuildId, string GuildName);

public interface IDiscordModule
{
    Task NotifyOwner(string message);
    Task NotifyOffline();

    /// <summary>Deletes every message ARI sent within the given window, across all reachable
    /// channels (guild text channels and the owner DM). Returns the number of messages deleted.</summary>
    Task<int> DeleteRecentMessagesAsync(TimeSpan window);

    /// <summary>Returns all voice channels across all guilds that the given user (by username) is currently in.</summary>
    IReadOnlyList<VoiceChannelInfo> GetVoiceChannelsForUser(string username);

    /// <summary>Joins the specified voice channel. Leaves any current channel in the same guild first.</summary>
    Task<string> JoinVoiceChannelAsync(ulong channelId);

    /// <summary>Leaves the voice channel in the specified guild, or all voice channels if guildId is null.</summary>
    Task<string> LeaveVoiceChannelAsync(ulong? guildId = null);
}

public interface ILLMModule
{
    Task StopAllServersAsync();
    Task RestartAllServersAsync();
    bool AssignAgentServer(string agentName, string serverName);
    bool AssignAgentSlot(string agentName, string? slotName);
    /// <summary>True when no thread is currently being processed — Ari is idle.</summary>
    bool IsIdle { get; }

    /// <summary>True when Ari is actively in conversation — any non-internal thread is in the
    /// Active or Streaming state (a live exchange or one still inside its response window). Used to
    /// defer background walks so they don't land mid-conversation.</summary>
    bool ConversationActive { get; }

    /// <summary>Master switch for background dreaming (checked by DreamOrchestrator on each idle tick).</summary>
    bool DreamingEnabled { get; set; }

    /// <summary>Runs a due reminder through a real agent turn: <paramref name="prompt"/> is what the
    /// reminder asked ARI to say/do, <paramref name="context"/> is the private briefing behind it
    /// (never shown to the user directly). The generated reply is delivered the same way a Dream wake
    /// is — a fresh proactive thread with a push notification.</summary>
    Task FireReminderAsync(string prompt, string context, string title, CancellationToken ct = default);
}

public interface IVoiceModule
{
    bool    IsReady      { get; }
    string? ActiveModel  { get; }
    string  ActiveEngine { get; }
    Task<byte[]> Synthesise(string text, CancellationToken ct = default);
    Task<byte[]> Synthesise(string text, Dictionary<string, object>? engineParams, CancellationToken ct = default);
    (float speed, float pauseScale) GetVoiceSettings();
    void SetVoiceSettings(float speed, float pauseScale);
    void Speak(string text);
    IReadOnlyList<EngineParameter> GetEngineParameters();
    Task SwitchEngine(string engine, string modelName, CancellationToken ct = default);
}

public interface IVoiceSynthesisModule
{
    bool IsSetupComplete { get; }
}

public interface IBrainModule { }

public interface IImageGenModule
{
    bool IsReady { get; }
    Task<byte[]> GenerateAsync(
        string   prompt,
        string   negativePrompt     = "",
        string   checkpointFilename = "",
        int      steps              = 25,
        int      width              = 1024,
        int      height             = 1024,
        long     seed               = -1,
        string[] referenceImages    = default!,
        float    denoise            = 1.0f,
        CancellationToken ct        = default);
    void Shutdown();
}

public enum RecurrenceFrequency { None, Daily, Weekly, Monthly, Yearly }

/// <summary>How a calendar entry repeats. Frequency.None means it never repeats. DaysOfWeek only
/// applies to Weekly ("every weekday" = Weekly, Interval 1, DaysOfWeek Mon-Fri). Until/Count are both
/// optional and independent — whichever is hit first ends the recurrence.</summary>
public record RecurrenceInfo(
    RecurrenceFrequency Frequency,
    int Interval = 1,
    IReadOnlyList<DayOfWeek>? DaysOfWeek = null,
    DateTime? Until = null,
    int? Count = null);

public record CalendarEventInfo(long Id, string Title, string? Notes, DateTime Start, DateTime End, bool IsWholeDay, RecurrenceInfo? Recurrence);

public record ReminderInfo(long Id, string Title, string? Notes, DateTime TriggerTime, string Prompt, string? Context, RecurrenceInfo? Recurrence);

public interface ICalendarModule
{
    long CreateEvent(string title, DateTime start, DateTime end, bool isWholeDay, string? notes, RecurrenceInfo? recurrence);

    long CreateReminder(string title, DateTime triggerTime, string prompt, string? context, string? notes, RecurrenceInfo? recurrence);

    /// <summary>Events overlapping the window [today, today + days) when days is positive, or
    /// [today + days, today) when negative — so a caller can ask for either "next N days" or
    /// "last N days" through the sign of one parameter instead of two near-identical methods.</summary>
    IReadOnlyList<CalendarEventInfo> ListEvents(int days);

    /// <summary>Reminders whose next occurrence falls in the same signed window as ListEvents.</summary>
    IReadOnlyList<ReminderInfo> ListReminders(int days);

    /// <summary>Events overlapping an explicit [start, end) window — for browsing an arbitrary month/week/day
    /// rather than a window anchored on today (see ListEvents).</summary>
    IReadOnlyList<CalendarEventInfo> ListEventsInRange(DateTime start, DateTime end);

    IReadOnlyList<ReminderInfo> ListRemindersInRange(DateTime start, DateTime end);

    CalendarEventInfo? GetEvent(long id);

    ReminderInfo? GetReminder(long id);

    bool UpdateEvent(long id, string title, DateTime start, DateTime end, bool isWholeDay, string? notes, RecurrenceInfo? recurrence);

    bool UpdateReminder(long id, string title, DateTime triggerTime, string prompt, string? context, string? notes, RecurrenceInfo? recurrence);

    bool DeleteEntry(long id);
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
    /// <summary>WebSocket URL of the running Whisper worker, e.g. ws://127.0.0.1:5555/ws. Null if not started.</summary>
    string? WhisperUrl { get; }
}

/// <summary>
/// Lets the control panel spin a module up or down without a restart. Implemented once, by ARI.Core's
/// top-level host (the only place that knows how to construct/tear down each module), and reached from
/// ARI.API through this interface so the two projects don't need a direct reference to each other.
/// Only modules with real start/stop semantics are covered here — LLM and API are load-bearing enough
/// (the panel making the request is itself served by API, and API depends on LLM) that they stay
/// restart-only, same as before. Brain is likewise excluded for now: it configures itself once inside
/// LLMModule's constructor rather than existing as an independently start/stoppable object.
/// </summary>
public interface IModuleLifecycle
{
    /// <summary>Starts the named module. Returns null on success, or an error/explanation string
    /// (e.g. "requires a restart", or a genuine failure) when it could not be started hot.</summary>
    Task<string?> StartModule(string key);

    /// <summary>Stops the named module. Same null-or-message contract as StartModule.</summary>
    Task<string?> StopModule(string key);
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
    public static IProjectService?       Projects       { get; private set; }
    public static IImageGenModule?       ImageGen       { get; private set; }
    public static ICalendarModule?       Calendar       { get; private set; }
    public static IModuleLifecycle?      Lifecycle      { get; private set; }

    public static void Register(
        IDiscordModule?        discord        = null,
        ILLMModule?            llm            = null,
        IVoiceModule?          voice          = null,
        IVoiceSynthesisModule? voiceSynthesis = null,
        IBrainModule?          brain          = null,
        IListenerModule?       listener       = null,
        IWebPushModule?        webPush        = null,
        IProjectService?       projects       = null,
        IImageGenModule?       imageGen       = null,
        ICalendarModule?       calendar       = null,
        IModuleLifecycle?      lifecycle      = null)
    {
        if (discord        is not null) Discord        = discord;
        if (llm            is not null) Llm            = llm;
        if (voice          is not null) Voice          = voice;
        if (voiceSynthesis is not null) VoiceSynthesis = voiceSynthesis;
        if (brain          is not null) Brain          = brain;
        if (listener       is not null) Listener       = listener;
        if (webPush        is not null) WebPush        = webPush;
        if (projects       is not null) Projects       = projects;
        if (imageGen       is not null) ImageGen       = imageGen;
        if (calendar       is not null) Calendar       = calendar;
        if (lifecycle      is not null) Lifecycle      = lifecycle;
    }

    // Register(...) only ever sets a slot — a stopped module needs to actually disappear (every
    // "is not null" check across the app is how the rest of ARI knows a module is live), which a
    // no-op null argument can't express. One explicit clear method per hot-stoppable module.
    public static void ClearVoice()          => Voice          = null;
    public static void ClearVoiceSynthesis() => VoiceSynthesis = null;
    public static void ClearListener()       => Listener       = null;
    public static void ClearDiscord()        => Discord        = null;
    public static void ClearImageGen()       => ImageGen       = null;
    public static void ClearCalendar()       => Calendar       = null;
}

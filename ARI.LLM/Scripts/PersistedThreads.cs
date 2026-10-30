using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// Full-fidelity snapshots of conversations, for the Persistent Threads setting. A snapshot is one JSON-lines file per
/// thread in <see cref="Paths.PersistedThreads"/>: line one is the thread's metadata, every line after it is one history
/// item. Attachment bodies live beside it in <c>{key}.files/</c>. It is rewritten after every completed exchange and once
/// more when the thread expires, so what is on disk is what was in memory — a thread loaded from it (to view, or to carry
/// on) has the same history, the same context window and the same owner.
///
/// This is deliberately not the UI's thread JSON, which leaves out the reasoning, the tool trace the model's context is
/// rebuilt from, hidden items and attachment bodies. Dialogue threads only for now.
/// </summary>
internal static class PersistedThreads
{
    private const int SchemaVersion = 1;
    private static readonly object WriteGate = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
    };

    // ── What gets saved ───────────────────────────────────────────────────────────

    /// <summary>A user-facing Dialogue conversation that belongs to somebody. Discord chats, internal worker threads and
    /// threads nobody owns are never saved.</summary>
    internal static bool IsEligible(Thread t) =>
        t.Pipeline == ThreadPipeline.Dialogue && !t.Internal && t.OwnerId is not null && t.HasUserMessages
        && !t.Key.StartsWith("dm:", StringComparison.OrdinalIgnoreCase)
        && !t.Key.StartsWith("guild:", StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldSave(Thread t) => PersistedThreadsSetting.Enabled && IsEligible(t);

    // ── Records ───────────────────────────────────────────────────────────────────

    internal sealed class Meta
    {
        public int      V               { get; set; } = SchemaVersion;
        public string   Key             { get; set; } = "";
        public string   Pipeline        { get; set; } = "Dialogue";
        public string?  OwnerId         { get; set; }
        public string?  Title           { get; set; }
        public string   Medium          { get; set; } = "Web";
        public ulong    DiscordChannelId { get; set; }   // before chat services were plugins; read so old threads still reply
        public string?  ChannelService { get; set; }
        public string?  ChannelTarget { get; set; }
        public string?  PlatformContext { get; set; }
        public string?  FilesystemRoot  { get; set; }
        public bool     IsBrainVault    { get; set; }
        public bool     IsRemoteProject { get; set; }
        public bool     IsAdminChat     { get; set; }
        public bool     HasMemoryAccess { get; set; } = true;
        public bool     SafeMode        { get; set; }
        public DateTime LastMessageAt   { get; set; }
        public int      ContextStart    { get; set; }
        public int      TurnSerial      { get; set; }
        public int      ItemCount       { get; set; }
        public DateTime SavedAt         { get; set; }
    }

    private sealed class AttachmentRec
    {
        public string  Id       { get; set; } = "";
        public string  Name     { get; set; } = "";
        public bool    IsImage  { get; set; }
        public string? MimeType { get; set; }
    }

    private sealed class ItemRec
    {
        public string   T       { get; set; } = "";          // prompt | internalPrompt | response | commandInput | commandResponse | engramEvent
        public DateTime Ts      { get; set; }
        public bool     Visible { get; set; } = true;
        public string?  Author  { get; set; }
        public string?  Text    { get; set; }                // prompt / internalPrompt / commandInput(Input) / commandResponse(Response)
        public List<AttachmentRec>? Attachments { get; set; }

        // Response
        public string?  State           { get; set; }
        public string?  Content         { get; set; }        // the reply as markup; ContentBlock.Parse turns it back into blocks
        public bool     Continued       { get; set; }
        public bool     Truncated       { get; set; }
        public bool     UiOnly          { get; set; }
        public double?  ThinkingSeconds { get; set; }
        public double?  PrefillSeconds  { get; set; }
        public double?  TypingSeconds   { get; set; }
        public double?  TotalSeconds    { get; set; }
        public double?  RecallSeconds   { get; set; }
        public int?     ToolCallCount   { get; set; }
        public string?  RecallNotes     { get; set; }
        public string?  ContextSummary  { get; set; }
        public List<WebSource>? WebSources { get; set; }
        public int?     AppraisalGrade   { get; set; }
        public int?     AppraisalSeconds { get; set; }
        public ResponseData? Data       { get; set; }
        public string?  Reasoning       { get; set; }
        public List<TraceStep>? Trace   { get; set; }

        // EngramEvent
        public List<NoteChange>? Changes { get; set; }
    }

    // ── Paths ─────────────────────────────────────────────────────────────────────

    private static string FileFor(string key)  => Path.Combine(Paths.PersistedThreads, $"{Safe(key)}.jsonl");
    private static string FilesDir(string key) => Path.Combine(Paths.PersistedThreads, $"{Safe(key)}.files");

    private static string Safe(string key)
    {
        StringBuilder sb = new(key.Length);
        foreach (char c in key)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.ToString();
    }

    // ── Save ──────────────────────────────────────────────────────────────────────

    internal static void Save(Thread thread)
    {
        if (!ShouldSave(thread)) return;
        try
        {
            Directory.CreateDirectory(Paths.PersistedThreads);

            // A snapshot is taken while the thread is quiet (after an exchange, or at expiry). Anything still streaming
            // would be half a reply, so it is left out.
            List<ThreadItem> items = thread.History.ToList().Where(i => i is not Response { State: State.Streaming }).ToList();

            Meta meta = new()
            {
                Key = thread.Key, Pipeline = thread.Pipeline.ToString(), OwnerId = thread.OwnerId, Title = thread.Title,
                Medium = thread.Medium.ToString(), ChannelService = thread.ChannelService, ChannelTarget = thread.ChannelTarget, PlatformContext = thread.PlatformContext,
                FilesystemRoot = thread.FilesystemRoot, IsBrainVault = thread.IsBrainVault,
                IsRemoteProject = thread.IsRemoteProject, IsAdminChat = thread.IsAdminChat, HasMemoryAccess = thread.HasMemoryAccess,
                SafeMode = thread.SafeMode, LastMessageAt = thread.LastMessageAt, ContextStart = thread.ContextStart,
                TurnSerial = thread.TurnSerial, ItemCount = items.Count, SavedAt = DateTime.UtcNow,
            };

            StringBuilder sb = new();
            sb.AppendLine(JsonSerializer.Serialize(meta, Json));
            foreach (ThreadItem item in items)
                if (ToRecord(thread.Key, item) is { } rec)
                    sb.AppendLine(JsonSerializer.Serialize(rec, Json));

            lock (WriteGate)
            {
                string file = FileFor(thread.Key);
                string tmp  = file + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                File.Move(tmp, file, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[PersistedThreads] could not save {Key}: {Error}", thread.Key, ex.Message);
        }
    }

    private static ItemRec? ToRecord(string key, ThreadItem item)
    {
        switch (item)
        {
            case Prompt p:
                return new ItemRec { T = "prompt", Ts = p.Timestamp, Visible = p.IsVisible, Author = p.AuthorName, Text = p.Text,
                                     Attachments = SaveAttachments(key, p.Attachments) };
            case InternalPrompt ip:
                return new ItemRec { T = "internalPrompt", Ts = ip.Timestamp, Visible = ip.IsVisible, Text = ip.Text };
            case Response r:
                return new ItemRec
                {
                    T = "response", Ts = r.Timestamp, Visible = r.IsVisible, State = r.State.ToString(),
                    Content = r.Content.Count > 0 ? string.Concat(r.Content.Select(b => b.ToString())) : r.ContentText,
                    Continued = r.Continued, Truncated = r.Truncated, UiOnly = r.UiOnly,
                    ThinkingSeconds = r.ThinkingSeconds, PrefillSeconds = r.PrefillSeconds, TypingSeconds = r.TypingSeconds,
                    TotalSeconds = r.TotalSeconds, RecallSeconds = r.RecallSeconds, ToolCallCount = r.ToolCallCount,
                    RecallNotes = r.RecallNotes, ContextSummary = r.ContextSummary, WebSources = r.WebSources,
                    AppraisalGrade = r.AppraisalGrade, AppraisalSeconds = r.AppraisalSeconds, Data = r.Data,
                    Reasoning = r.Reasoning, Trace = r.Trace,
                };
            case CommandInput ci:
                return new ItemRec { T = "commandInput", Ts = ci.Timestamp, Visible = ci.IsVisible, Text = ci.Input };
            case CommandResponse cr:
                return new ItemRec { T = "commandResponse", Ts = cr.Timestamp, Visible = cr.IsVisible, Text = cr.Response };
            case EngramEvent ee:
                return new ItemRec { T = "engramEvent", Ts = ee.Timestamp, Visible = ee.IsVisible, Changes = ee.Changes.ToList() };
            default:
                return null;
        }
    }

    private static List<AttachmentRec>? SaveAttachments(string key, List<Attachment>? attachments)
    {
        if (attachments is not { Count: > 0 }) return null;
        string dir = FilesDir(key);
        Directory.CreateDirectory(dir);
        foreach (Attachment a in attachments)
        {
            string path = Path.Combine(dir, Safe(a.Id));
            if (!File.Exists(path)) File.WriteAllText(path, a.Content);
        }
        return attachments.Select(a => new AttachmentRec { Id = a.Id, Name = a.Name, IsImage = a.IsImage, MimeType = a.MimeType }).ToList();
    }

    // ── Read ──────────────────────────────────────────────────────────────────────

    internal static bool Exists(string key) => File.Exists(FileFor(key));

    /// <summary>The metadata line of one saved thread, or null if there is no usable file.</summary>
    internal static Meta? ReadMeta(string key) => ReadMetaFile(FileFor(key));

    /// <summary>Every saved thread's metadata, newest first.</summary>
    internal static List<Meta> ListMeta()
    {
        if (!Directory.Exists(Paths.PersistedThreads)) return new();
        List<Meta> all = new();
        foreach (string file in Directory.EnumerateFiles(Paths.PersistedThreads, "*.jsonl"))
            if (ReadMetaFile(file) is { } m) all.Add(m);
        return all.OrderByDescending(m => m.LastMessageAt).ToList();
    }

    private static Meta? ReadMetaFile(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using StreamReader reader = new(file);
            string? first = reader.ReadLine();
            return first is null ? null : JsonSerializer.Deserialize<Meta>(first, Json);
        }
        catch { return null; }
    }

    /// <summary>Fills <paramref name="thread"/> (a freshly created, empty Dialogue thread) from its saved snapshot.</summary>
    internal static bool Restore(string key, Thread thread)
    {
        string file = FileFor(key);
        if (!File.Exists(file)) return false;
        try
        {
            string[] lines = File.ReadAllLines(file);
            Meta? meta = JsonSerializer.Deserialize<Meta>(lines[0], Json);
            if (meta is null) return false;

            thread.OwnerId         = meta.OwnerId;
            thread.Title           = meta.Title;
            thread.Medium          = meta.Medium == "Discord" ? ThreadMedium.Channel : Enum.TryParse(meta.Medium, out ThreadMedium medium) ? medium : ThreadMedium.Web;
            thread.ChannelService  = meta.ChannelService ?? (meta.Medium == "Discord" ? "discord" : null);
            thread.ChannelTarget   = meta.ChannelTarget ?? (meta.DiscordChannelId != 0 ? meta.DiscordChannelId.ToString() : null);
            thread.PlatformContext ??= meta.PlatformContext;
            thread.FilesystemRoot  = meta.FilesystemRoot;
            thread.IsBrainVault    = meta.IsBrainVault;
            thread.IsRemoteProject = meta.IsRemoteProject;
            thread.IsAdminChat     = meta.IsAdminChat;
            thread.HasMemoryAccess = meta.HasMemoryAccess;
            thread.SafeMode        = meta.SafeMode;
            thread.TurnSerial      = meta.TurnSerial;

            foreach (string line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                ItemRec? rec = JsonSerializer.Deserialize<ItemRec>(line, Json);
                if (rec is not null && FromRecord(key, rec) is { } item) thread.History.Add(item);
            }

            thread.ContextStart    = meta.ContextStart;
            thread.LastMessageAt   = meta.LastMessageAt;
            thread.EngramProcessed = true;   // everything saved here has already been through Engram
            return true;
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[PersistedThreads] could not load {Key}: {Error}", key, ex.Message);
            return false;
        }
    }

    private static ThreadItem? FromRecord(string key, ItemRec r)
    {
        switch (r.T)
        {
            case "prompt":
                return new Prompt
                {
                    Text = r.Text ?? "", AuthorName = r.Author ?? "User", Timestamp = r.Ts, IsVisible = r.Visible,
                    Attachments = LoadAttachments(key, r.Attachments),
                };
            case "internalPrompt":
                return new InternalPrompt { Text = r.Text ?? "", Timestamp = r.Ts, IsVisible = r.Visible };
            case "response":
                return new Response
                {
                    Timestamp = r.Ts, IsVisible = r.Visible,
                    State = Enum.TryParse(r.State, out State s) ? s : State.Complete,
                    Content = ContentBlock.Parse(r.Content ?? ""),
                    Continued = r.Continued, Truncated = r.Truncated, UiOnly = r.UiOnly,
                    ThinkingSeconds = r.ThinkingSeconds, PrefillSeconds = r.PrefillSeconds, TypingSeconds = r.TypingSeconds,
                    TotalSeconds = r.TotalSeconds, RecallSeconds = r.RecallSeconds, ToolCallCount = r.ToolCallCount,
                    RecallNotes = r.RecallNotes, ContextSummary = r.ContextSummary, WebSources = r.WebSources,
                    AppraisalGrade = r.AppraisalGrade, AppraisalSeconds = r.AppraisalSeconds,
                    Data = r.Data ?? new ResponseData(), Reasoning = r.Reasoning, Trace = r.Trace,
                };
            case "commandInput":
                return new CommandInput { Input = r.Text ?? "", Timestamp = r.Ts, IsVisible = r.Visible };
            case "commandResponse":
                return new CommandResponse { Response = r.Text ?? "", Timestamp = r.Ts, IsVisible = r.Visible };
            case "engramEvent":
                return new EngramEvent { Changes = r.Changes ?? new(), Timestamp = r.Ts, IsVisible = r.Visible };
            default:
                return null;
        }
    }

    private static List<Attachment>? LoadAttachments(string key, List<AttachmentRec>? recs)
    {
        if (recs is not { Count: > 0 }) return null;
        string dir = FilesDir(key);
        return recs.Select(a =>
        {
            string path = Path.Combine(dir, Safe(a.Id));
            return new Attachment
            {
                Id = a.Id, Name = a.Name, IsImage = a.IsImage, MimeType = a.MimeType,
                Content = File.Exists(path) ? File.ReadAllText(path) : "",
            };
        }).ToList();
    }

    // ── Delete ────────────────────────────────────────────────────────────────────

    /// <summary>Removes one saved thread for good: its snapshot, its attachments and its scratchpad.</summary>
    internal static void Delete(string key)
    {
        try
        {
            Meta? meta = ReadMeta(key);
            lock (WriteGate)
            {
                File.Delete(FileFor(key));
                if (Directory.Exists(FilesDir(key))) Directory.Delete(FilesDir(key), recursive: true);
            }
            if (meta?.FilesystemRoot is { } root && root.StartsWith(Paths.ServerDir("Scratchpad"), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[PersistedThreads] could not delete {Key}: {Error}", key, ex.Message);
        }
    }

    /// <summary>Removes every saved thread. Returns how many there were.</summary>
    internal static int DeleteAll()
    {
        int count = 0;
        foreach (Meta m in ListMeta()) { Delete(m.Key); count++; }
        return count;
    }

    internal static (int Count, long Bytes) Stats()
    {
        if (!Directory.Exists(Paths.PersistedThreads)) return (0, 0);
        int count = Directory.EnumerateFiles(Paths.PersistedThreads, "*.jsonl").Count();
        long bytes = Directory.EnumerateFiles(Paths.PersistedThreads, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        return (count, bytes);
    }
}

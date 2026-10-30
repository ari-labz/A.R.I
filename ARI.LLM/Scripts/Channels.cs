using System.Text;
using ARI.Common;
using ARI.Sdk;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// Everything between a chat service and Ari's pipelines, for every service the same way. A plugin that implements <see cref="IChannel"/>
/// hands each message to <see cref="Receive"/>; this finds the person, picks the thread, decides whether she answers, runs the pipeline and
/// gives the reply back to the plugin. A plugin that implements <see cref="INotifier"/> receives <see cref="Notify"/>.
/// </summary>
public class Channels : IDisposable
{
    private const string PASS_TOKEN = "[PASS]";
    private const string AWARENESS_GATE = "awareness";
    private const int TYPING_INTERVAL_MS = 8000;
    private static readonly TimeSpan SWEEP_INTERVAL = TimeSpan.FromMinutes(1);

    private static readonly HashSet<string> READABLE_EXTENSIONS = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".cs", ".py", ".js", ".ts", ".json", ".yaml", ".yml", ".toml", ".xml", ".html", ".css", ".sh", ".log" };

    private readonly LLMModule llm;
    private readonly Dictionary<string, string> owners = new();
    private readonly CancellationTokenSource stopping = new();

    public Conversations Conversations { get; }

    public Channels(LLMModule llm)
    {
        this.llm = llm;
        Conversations = new Conversations(this);
        llm.ThreadWoke += (threadKey, reply) => _ = DeliverWoken(threadKey, reply);
        _ = SweepIdle();
    }

    public void Dispose() => stopping.Cancel();

    /// <summary>Whether a thread key belongs to a conversation on a chat service rather than the app.</summary>
    public static bool IsChannelKey(string threadKey) =>
        threadKey.StartsWith("dm:", StringComparison.OrdinalIgnoreCase) || threadKey.StartsWith("guild:", StringComparison.OrdinalIgnoreCase);

    public void LinkOwner(string service, string id) => owners[service] = id;

    public bool IsOwner(string service, string id) => owners.TryGetValue(service, out string? owner) && owner == id;

    /// <summary>The running plugin that carries this service.</summary>
    public IChannel? Find(string service)
    {
        foreach (IPlugin plugin in PluginRegistry.All)
            if (plugin is IChannel channel && channel.Service == service && plugin.State == PluginState.Running) return channel;
        return null;
    }

    public string? NameOf(string service, string id) => Find(service)?.NameOf(id);

    public async Task Receive(Message message, Handling handling)
    {
        IChannel? channel = Find(message.Service);
        if (channel is null) return;

        bool isOwner = IsOwner(message.Service, message.SenderId);
        Conversations.Chat? chat = message.IsDirect ? Conversations.OpenFor(message.Service, message.SenderId) : null;

        // Anyone but the owner reaches her in a DM only through a conversation she opened.
        if (message.IsDirect && !isOwner && chat is null) return;

        string threadKey = chat?.ThreadKey ?? (message.IsDirect ? $"dm:{message.SenderId}" : $"guild:{message.ConversationId}");
        if (chat is not null) Conversations.Touch(message.Service, message.SenderId);

        string ownerTag = isOwner && !message.IsDirect ? " [MASTER]" : "";
        string prompt = $"[{message.Sent.LocalDateTime:dd/MM/yyyy HH:mm}] [{message.SenderName}{ownerTag} {message.Place}]: {message.Text}";

        if (!message.Addressed && handling.Gate == AWARENESS_GATE && llm.AwarenessAvailable)
        {
            if (!await llm.EvaluateTextAwareness(threadKey, prompt))
            {
                Shared.Logger.LogInformation("[Channels] Gate: not answering in [{ThreadKey}]", threadKey);
                return;
            }
        }

        Thread thread = llm.GetOrCreateDialogueThread(threadKey);
        thread.Medium = ThreadMedium.Channel;
        thread.ChannelService = message.Service;
        thread.ChannelTarget = message.Target;
        if (message.IsDirect && isOwner && Shared.MasterUserId is string masterId) thread.OwnerId ??= masterId;
        ToolFactories.LoadGroup($"{message.Service}_tools", thread);

        List<Attachment>? attachments = message.Files.Count > 0 ? await SaveUploads(message.Files, threadKey) : null;
        Conversation where = ConversationOf(thread);

        using CancellationTokenSource typing = new();
        _ = KeepTyping(channel, where, typing.Token);
        try
        {
            string response = await llm.Prompt(threadKey, prompt, message.SenderName, message.Context, attachments);
            typing.Cancel();

            string text = ContentBlock.PlainText(response);
            if (text.Length == 0 || text.Equals(PASS_TOKEN, StringComparison.OrdinalIgnoreCase))
            {
                Shared.Logger.LogInformation("[Channels] She chose not to answer in [{ThreadKey}]", threadKey);
                return;
            }

            Shared.Logger.LogInformation("[Channels] Reply to {Name} [{ThreadKey}]", message.SenderName, threadKey);
            await channel.Deliver(new Reply { Conversation = where, Text = text });
        }
        catch (LlmRequestFailedException ex)
        {
            typing.Cancel();
            Shared.Logger.LogError("[Channels] The model request failed: {Error}", ex.Message);
            await channel.Deliver(new Reply { Conversation = where, Text = "A·R·I is unable to respond right now.", IsError = true });
        }
        catch (ModelNotFoundException ex)
        {
            typing.Cancel();
            Shared.Logger.LogError("[Channels] The dialogue model isn't available: {Error}", ex.Message);
            await channel.Deliver(new Reply { Conversation = where, Text = "A·R·I is unable to respond right now.", IsError = true });
        }
    }

    public Task<string?> Command(string text, bool isOwner) => llm.HandleCommand(null, text, isOwner);

    /// <summary>A reply from a thread that something else woke (a conversation's report) goes where that conversation is.</summary>
    private async Task DeliverWoken(string threadKey, string reply)
    {
        string text = ContentBlock.PlainText(reply);
        if (text.Length == 0 || text.Equals(PASS_TOKEN, StringComparison.OrdinalIgnoreCase)) return;
        if (!IsChannelKey(threadKey)) return;

        Thread thread = llm.GetOrCreateDialogueThread(threadKey);
        IChannel? channel = thread.ChannelService is null ? null : Find(thread.ChannelService);
        if (channel is null) return;

        try { await channel.Deliver(new Reply { Conversation = ConversationOf(thread), Text = text }); }
        catch (Exception ex) { Shared.Logger.LogWarning("[Channels] Couldn't deliver a woken reply [{ThreadKey}]: {Error}", threadKey, ex.Message); }
    }

    /// <summary>Sends a file to where this thread's conversation is. Returns null on success, otherwise why not.</summary>
    public async Task<string?> SendFile(Thread thread, string path, string? text)
    {
        IChannel? channel = thread.ChannelService is null ? null : Find(thread.ChannelService);
        if (channel is null || string.IsNullOrEmpty(thread.ChannelTarget)) return "this conversation has no channel to send it to";
        return await channel.SendFile(ConversationOf(thread), path, text);
    }

    public async Task Notify(Notice notice)
    {
        foreach (IPlugin plugin in PluginRegistry.All)
        {
            if (plugin is not INotifier notifier || plugin.State != PluginState.Running) continue;
            try { await notifier.Notify(notice); }
            catch (Exception ex) { Shared.Logger.LogWarning("[Channels] {Plugin} couldn't deliver a notice: {Error}", plugin.Name, ex.Message); }
        }
    }

    private static Conversation ConversationOf(Thread thread)
    {
        string[] parts = thread.Key.Split(':');
        return new Conversation
        {
            Service = thread.ChannelService ?? "",
            Id = parts.Length > 1 ? parts[1] : "",
            Target = thread.ChannelTarget ?? "",
            IsDirect = parts[0] == "dm",
        };
    }

    private static async Task KeepTyping(IChannel channel, Conversation conversation, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await channel.Typing(conversation);
            try { await Task.Delay(TYPING_INTERVAL_MS, ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task SweepIdle()
    {
        using PeriodicTimer timer = new(SWEEP_INTERVAL);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping.Token))
                foreach (string threadKey in Conversations.CloseIdle())
                {
                    Shared.Logger.LogInformation("[Channels] Closed an idle conversation [{ThreadKey}]", threadKey);
                    await llm.CloseThreadAsync(threadKey);
                }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task<List<Attachment>> SaveUploads(List<Upload> uploads, string threadKey)
    {
        List<Attachment> saved = new();
        foreach (Upload upload in uploads)
        {
            bool isImage = upload.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            if (!isImage && !READABLE_EXTENSIONS.Contains(Path.GetExtension(upload.Name))) continue;

            byte[] bytes = await upload.Read();
            string name = upload.Name;
            if (isImage)
            {
                // Images repeat names ("image.png"), so the copy kept for generate_image gets a unique suffix.
                string safe = Path.GetFileName(upload.Name);
                name = $"{Path.GetFileNameWithoutExtension(safe)}-{Guid.NewGuid().ToString("N")[..6]}{Path.GetExtension(safe)}";
                string folder = Paths.ScratchpadDir(threadKey);
                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(Path.Combine(folder, name), bytes);
            }

            string content = isImage ? Convert.ToBase64String(bytes) : Encoding.UTF8.GetString(bytes);
            saved.Add(new Attachment { Id = Guid.NewGuid().ToString("N"), Name = name, Content = content, IsImage = isImage, MimeType = upload.MimeType });
        }
        return saved;
    }
}

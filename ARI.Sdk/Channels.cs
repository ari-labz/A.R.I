namespace ARI.Sdk;

/// <summary>A file someone sent with a message. It is only fetched if ARI wants it, so a big video costs nothing.</summary>
public class Upload
{
    public string Name = "";
    public string MimeType = "application/octet-stream";
    public Func<Task<byte[]>> Read = () => Task.FromResult(Array.Empty<byte>());
}

/// <summary>A message from a person on a service, as a channel plugin hands it to ARI.</summary>
public class Message
{
    public string Service = "";
    public string SenderId = "";
    public string SenderName = "";

    /// <summary>The direct chat or group this message is in. Replies and later messages from the same place share a thread.</summary>
    public string ConversationId = "";

    /// <summary>Where a reply goes on the service (a channel id), when that differs from the conversation.</summary>
    public string Target = "";

    public bool IsDirect;

    /// <summary>She was spoken to by name, so there is nothing to decide.</summary>
    public bool Addressed = true;

    /// <summary>Shown to her beside the sender's name: "via DM", "in #general".</summary>
    public string Place = "";

    /// <summary>What she should know about this place, such as who her owner is on this service.</summary>
    public string? Context;

    public string Text = "";
    public DateTimeOffset Sent = DateTimeOffset.Now;
    public List<Upload> Files = [];
}

/// <summary>Where a reply, a typing indicator or a file goes.</summary>
public class Conversation
{
    public string Service = "";
    public string Id = "";
    public string Target = "";
    public bool IsDirect;
}

public class Reply
{
    public Conversation Conversation = new();
    public string Text = "";

    /// <summary>She could not answer, and this says so plainly.</summary>
    public bool IsError;
}

/// <summary>How ARI treats a message after the plugin hands it over.</summary>
public class Handling
{
    /// <summary>What decides whether she answers a message that wasn't addressed to her: the built-in "awareness" check, or null to always answer.</summary>
    public string? Gate;
}

public enum NoticeKind { Info, Online, Offline }

public class Notice
{
    public string Text = "";
    public NoticeKind Kind = NoticeKind.Info;
}

/// <summary>A plugin that people talk to Ari through. ARI uses it while the plugin is running.</summary>
public interface IChannel
{
    string Service { get; }

    /// <summary>Sends her words to the person, already free of anything meant for the app's chat.</summary>
    Task Deliver(Reply reply);

    Task Typing(Conversation conversation);

    /// <summary>Returns null on success, otherwise why the file could not be sent.</summary>
    Task<string?> SendFile(Conversation conversation, string path, string? text);

    /// <summary>A person's display name if the plugin has seen them. Never waits.</summary>
    string? NameOf(string externalId);
}

/// <summary>A plugin that can tell the owner something.</summary>
public interface INotifier
{
    Task Notify(Notice notice);
}

/// <summary>What ARI gives every plugin to reach the rest of ARI.</summary>
public interface IAri
{
    /// <summary>Finds the person, picks the thread, decides whether she answers, runs her pipeline and delivers the reply through the plugin's <see cref="IChannel"/>.</summary>
    Task Receive(Message message, Handling handling);

    /// <summary>Runs a slash command such as /status. Returns what to show, or null if it isn't one.</summary>
    Task<string?> Command(string text, bool isOwner);

    /// <summary>Says that this id on this service is the owner. Only the plugin the owner configured should call it.</summary>
    void LinkOwner(string service, string id);
}

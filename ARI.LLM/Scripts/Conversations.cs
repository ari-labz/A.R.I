using System.Collections.Concurrent;

namespace ARI.LLM;

/// <summary>
/// Conversations Ari started with a person on a chat service, such as a DM from the discord_dm_user tool. Each has its own thread and a
/// brief from the thread that opened it. While one is open, everything that person sends goes into it, and Ari reports back to the opener
/// with report_to_parent (see <see cref="ParentReports"/>). It ends when she closes it, or when it sits idle.
/// </summary>
public class Conversations(Channels channels)
{
    public static readonly TimeSpan IDLE_TIMEOUT = TimeSpan.FromMinutes(30);

    /// <summary>What a report from a conversation starts with when the tool that opened it hands the report back.</summary>
    internal const string REPORT_PREFIX = "Report from the conversation: ";

    public class Chat
    {
        public required string Service { get; init; }
        public required string PersonId { get; init; }
        public required string ThreadKey { get; init; }
        public DateTime LastActivity { get; internal set; } = DateTime.Now;

        /// <summary>The thread that last messaged this person, which gets the reports.</summary>
        internal string? ParentKey;
    }

    private readonly ConcurrentDictionary<string, Chat> open = new();

    private static string KeyOf(string service, string personId) => $"{service}:{personId}";

    public Chat? OpenFor(string service, string personId) => open.TryGetValue(KeyOf(service, personId), out Chat? chat) ? chat : null;

    internal (Chat Chat, bool IsNew) GetOrOpen(string service, string personId)
    {
        bool created = false;
        Chat chat = open.GetOrAdd(KeyOf(service, personId), key =>
        {
            created = true;
            return new Chat { Service = service, PersonId = personId, ThreadKey = $"dm:{personId}:{DateTime.Now:yyyyMMddHHmmss}" };
        });
        chat.LastActivity = DateTime.Now;
        return (chat, created);
    }

    internal Chat? ForThread(string threadKey) => open.Values.FirstOrDefault(chat => chat.ThreadKey == threadKey);

    public void Touch(string service, string personId)
    {
        Chat? chat = OpenFor(service, personId);
        if (chat is not null) chat.LastActivity = DateTime.Now;
    }

    /// <summary>Closes the conversation; the person's next message starts a new thread. Returns its thread key.</summary>
    public string? Close(string service, string personId) => open.TryRemove(KeyOf(service, personId), out Chat? chat) ? chat.ThreadKey : null;

    /// <summary>Closes every conversation that has sat idle, telling the thread that opened each. Returns their thread keys.</summary>
    internal List<string> CloseIdle()
    {
        List<string> closed = new();
        foreach (Chat chat in open.Values.Where(chat => DateTime.Now - chat.LastActivity > IDLE_TIMEOUT).ToList())
        {
            if (!open.TryRemove(KeyOf(chat.Service, chat.PersonId), out _)) continue;
            if (chat.ParentKey is not null)
            {
                string name = channels.NameOf(chat.Service, chat.PersonId) ?? $"{chat.Service} user {chat.PersonId}";
                ParentReports.Send(chat.ParentKey, chat.ThreadKey, $"[Your DM conversation with {name} closed after {IDLE_TIMEOUT.TotalMinutes:0} minutes without a result.]");
            }
            closed.Add(chat.ThreadKey);
        }
        return closed;
    }
}

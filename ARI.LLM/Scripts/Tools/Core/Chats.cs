using System.Text;
using System.Text.RegularExpressions;

namespace ARI.LLM;

// Reads the transcripts ChatHistoryLogger keeps (one file per conversation) so Ari can look at what was said elsewhere when
// she's missing context. Owner only: these are people's private conversations.
internal static class ChatTranscripts
{
    internal static readonly Regex Stamp = new(@"^\[\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\] ", RegexOptions.Multiline | RegexOptions.Compiled);

    internal static string Folder => ARI.Common.Paths.ChatHistory;

    /// <summary>Splits a transcript into its messages: each starts with a [timestamp] and may run over several lines.</summary>
    internal static List<string> Messages(string text)
    {
        MatchCollection ms = Stamp.Matches(text);
        List<string> list = new();
        for (int i = 0; i < ms.Count; i++)
            list.Add(text[ms[i].Index..(i + 1 < ms.Count ? ms[i + 1].Index : text.Length)].Trim());
        return list;
    }

    internal static string Header(string text, string field)
    {
        Match m = Regex.Match(text, $@"^{field}:\s*(.*)$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }
}

[Tool("list_chats", "List the most recent conversations (in the app, on Discord, background ones), newest first, with who last spoke. " +
                    "Use it when you feel you're missing context, or the user mentions something said elsewhere, then open one with read_chat.",
      Access = ToolAccess.Read)]
public class ListChats : Tool
{
    [Param("How many to list. Default 10, at most 40.", Optional = true)]
    public int Count = 10;

    public override string OnValidate() => CallThread is { IsGuarded: false } ? null : "Only the owner's own conversations can see other chats.";

    public override Task<string> Run()
    {
        if (!Directory.Exists(ChatTranscripts.Folder)) return Task.FromResult("There are no saved conversations yet.");
        StringBuilder sb = new();
        foreach (FileInfo f in new DirectoryInfo(ChatTranscripts.Folder).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTime).Take(Math.Clamp(Count, 1, 40)))
        {
            string text = File.ReadAllText(f.FullName);
            List<string> msgs = ChatTranscripts.Messages(text);
            string label = ChatTranscripts.Header(text, "Label");
            string last = msgs.LastOrDefault() ?? "(empty)";
            last = Regex.Replace(last, @"^\[[^\]]+\] ", "").Replace('\n', ' ');
            sb.AppendLine($"- {Path.GetFileNameWithoutExtension(f.Name)}{(label.Length > 0 ? $" ({label})" : "")} · {msgs.Count} messages · last active {f.LastWriteTime:yyyy-MM-dd HH:mm}" +
                          $"{(f.Name.StartsWith(ConversationId.Replace(':', '_')) ? " · THIS conversation" : "")}\n    last: {(last.Length > 110 ? last[..110] + "…" : last)}");
        }
        return Task.FromResult(sb.Length == 0 ? "There are no saved conversations yet." : sb.ToString().TrimEnd());
    }
}

[Tool("read_chat", "Read the most recent messages of another conversation, to understand something you're missing. Find the conversation name with list_chats.",
      Access = ToolAccess.Read)]
public class ReadChat : Tool
{
    [Param("The conversation's name, exactly as list_chats shows it.")]
    public string Chat;

    [Param("How many of the latest messages to read. Default 20, at most 100.", Optional = true)]
    public int Messages = 20;

    [Param("Only show messages containing this text.", Optional = true)]
    public string Search;

    public override string OnValidate() => CallThread is { IsGuarded: false } ? null : "Only the owner's own conversations can see other chats.";

    public override Task<string> Run()
    {
        string name = Path.GetFileNameWithoutExtension(Path.GetFileName(Chat.Trim()));
        string file = Path.Combine(ChatTranscripts.Folder, name + ".txt");
        if (!File.Exists(file)) return Task.FromResult($"[Error: there is no conversation called '{name}'. Use list_chats to see the names.]");

        List<string> msgs = ChatTranscripts.Messages(File.ReadAllText(file));
        if (!string.IsNullOrWhiteSpace(Search)) msgs = msgs.Where(m => m.Contains(Search, StringComparison.OrdinalIgnoreCase)).ToList();
        int take = Math.Clamp(Messages, 1, 100);
        string text = string.Join("\n\n", msgs.Skip(Math.Max(0, msgs.Count - take)));
        if (text.Length > 14_000) text = "…" + text[^14_000..];
        return Task.FromResult(text.Length == 0 ? "No matching messages." : $"Last {Math.Min(take, msgs.Count)} of {msgs.Count} messages in {name}:\n\n{text}");
    }
}

[Tool("search_chats", "Search every saved conversation for a word or phrase, to find what was said about something: a decision, a name, a file, something the user told you. " +
                      "Returns where it appears with a little context; then open that conversation with read_chat.", Access = ToolAccess.Read)]
public class SearchChats : Tool
{
    [Param("The word or phrase to look for.")]
    public string Query;

    [Param("How many matches to show. Default 12, at most 40.", Optional = true)]
    public int Count = 12;

    public override string OnValidate() => CallThread is { IsGuarded: false } ? null : "Only the owner's own conversations can see other chats.";

    public override Task<string> Run()
    {
        if (string.IsNullOrWhiteSpace(Query)) return Task.FromResult("[Error: say what to search for.]");
        if (!Directory.Exists(ChatTranscripts.Folder)) return Task.FromResult("There are no saved conversations yet.");
        int limit = Math.Clamp(Count, 1, 40);
        StringBuilder sb = new();
        int hits = 0;
        foreach (FileInfo f in new DirectoryInfo(ChatTranscripts.Folder).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTime))
        {
            foreach (string m in ChatTranscripts.Messages(File.ReadAllText(f.FullName)))
            {
                int at = m.IndexOf(Query, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                string stamp = m.Length > 21 ? m[1..20] : "";
                string body = Regex.Replace(m, @"^\[[^\]]+\] ", "").Replace('\n', ' ');
                int from = Math.Max(0, body.IndexOf(Query, StringComparison.OrdinalIgnoreCase) - 70);
                string snippet = body.Substring(from, Math.Min(body.Length - from, 200));
                sb.AppendLine($"- {Path.GetFileNameWithoutExtension(f.Name)} · {stamp}: …{snippet}…");
                if (++hits >= limit) return Task.FromResult($"First {limit} matches for \"{Query}\" (newest conversations first; refine the search for fewer):\n{sb.ToString().TrimEnd()}");
            }
        }
        return Task.FromResult(hits == 0 ? $"No conversation mentions \"{Query}\"." : $"{hits} match{(hits == 1 ? "" : "es")} for \"{Query}\":\n{sb.ToString().TrimEnd()}");
    }
}

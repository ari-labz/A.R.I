using ARI.Common;
using Microsoft.Extensions.Logging;
using System.Text;

namespace ARI.LLM;

/// <summary>
/// Maintains a plain-text transcript of each user-facing thread under ARI/chat_history (one .txt
/// file per thread, named by thread key). Rewritten after every completed exchange so the file
/// always reflects the full conversation.
///
/// Purpose: zip the folder and hand it to Claude when reporting an issue, instead of screenshots.
/// Best-effort — any failure here is swallowed so it can never disrupt a conversation.
/// </summary>
internal static class ChatHistoryLogger
{
    private static readonly object WriteGate = new();

    /// <summary>Rewrites the transcript file for <paramref name="thread"/> from its full history.</summary>
    internal static void Write(Thread thread)
    {
        try
        {
            string file = Path.Combine(LogPaths.Dir("chat_history"), $"{Sanitize(thread.Key)}.txt");

            StringBuilder sb = new();
            sb.AppendLine($"Thread:   {thread.Key}");
            if (!string.IsNullOrWhiteSpace(thread.Label)) sb.AppendLine($"Label:    {thread.Label}");
            sb.AppendLine($"Pipeline: {thread.Pipeline}");
            sb.AppendLine(new string('=', 70));
            sb.AppendLine();

            foreach (ThreadItem item in thread.History)
            {
                string? line = Render(item);
                if (line is null) continue;
                sb.AppendLine(line);
                sb.AppendLine();
            }

            lock (WriteGate)
                File.WriteAllText(file, sb.ToString());
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[ChatHistoryLogger] failed to write transcript for {Key}: {Error}", thread.Key, ex.Message);
        }
    }

    /// <summary>Renders the user/ARI turns. Streaming/cancelled responses and internal items are skipped.</summary>
    private static string? Render(ThreadItem item) => item switch
    {
        UserMessage u => $"[{u.Timestamp:yyyy-MM-dd HH:mm:ss}] {u.Username}: {u.Content}{Attachments(u)}",
        AriResponse { State: AriResponseState.Complete } r => $"[{r.Timestamp:yyyy-MM-dd HH:mm:ss}] ARI: {r.ContentText}",
        _ => null,
    };

    private static string Attachments(UserMessage u) =>
        u.Attachments is { Count: > 0 } a ? $"  [attachments: {string.Join(", ", a.Select(x => x.Name))}]" : "";

    private static string Sanitize(string name)
    {
        StringBuilder sb = new(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c is ':' or ' ' ? '_' : c);
        return sb.ToString();
    }
}

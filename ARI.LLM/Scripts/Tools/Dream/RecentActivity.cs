using System.Text.RegularExpressions;
using ARI.Common;

namespace ARI.LLM;

/// <summary>
/// Ground truth for "how long has it actually been since we talked" — reads timestamps straight
/// from ChatLogs rather than the Brain vault. Vault notes are Engram's distilled output, written
/// only once a conversation thread goes dormant, so a conversation from ten minutes ago may not be
/// in there yet; treating that gap as "they've gone quiet" is exactly the mistake this exists to
/// prevent. Returns timestamps only, not message content — the content record stays Engram's job.
/// </summary>
internal sealed partial class RecentActivity : Tool
{
    internal override string     Name   => "recent_activity";
    internal override ToolAccess Access => ToolAccess.Read;

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "recent_activity",
            description = "Returns when your owner actually last messaged you, plus how many of the last 7 days had any conversation at all — read straight from the real chat log timestamps, not your memory notes (which can lag behind by hours). Call this before saying or implying 'you've been quiet' or 'it's been a while' — don't infer silence from an empty search_brain result alone. Returns timestamps only, no message content.",
            parameters  = new { type = "object", properties = new { } }
        }
    };

    [GeneratedRegex(@"^\[(\d{2}:\d{2}:\d{2})\]\s*Xywren:", RegexOptions.Multiline)]
    private static partial Regex OwnerLine();

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (!Directory.Exists(Paths.ChatLogs))
            return Task.FromResult<ToolResult>("No chat logs found yet.");

        DateTime? lastMessage = null;
        HashSet<DateOnly> activeDays = [];
        DateTime cutoff = DateTime.Now.AddDays(-7);

        IEnumerable<string> dayDirs = Directory.GetDirectories(Paths.ChatLogs)
            .Select(Path.GetFileName)
            .Where(name => DateOnly.TryParse(name, out _))!
            .Cast<string>()
            .OrderByDescending(name => name);

        foreach (string dayName in dayDirs)
        {
            DateOnly day = DateOnly.Parse(dayName);
            if (day.ToDateTime(TimeOnly.MinValue) < cutoff.Date && lastMessage is not null) break;

            string dir = Path.Combine(Paths.ChatLogs, dayName);
            foreach (string file in Directory.GetFiles(dir, "*.md"))
            {
                string text;
                try { text = File.ReadAllText(file); } catch { continue; }

                MatchCollection matches = OwnerLine().Matches(text);
                if (matches.Count == 0) continue;

                DateTime dayStart = day.ToDateTime(TimeOnly.MinValue);
                if (dayStart >= cutoff.Date) activeDays.Add(day);

                foreach (Match m in matches)
                {
                    if (!TimeOnly.TryParse(m.Groups[1].Value, out TimeOnly time)) continue;
                    DateTime ts = day.ToDateTime(time);
                    if (lastMessage is null || ts > lastMessage) lastMessage = ts;
                }
            }
        }

        if (lastMessage is null)
            return Task.FromResult<ToolResult>("No recorded messages from your owner in the logs — treat this as genuinely unknown, not as 'they've never talked to you'; the log retention window may simply not go back far enough.");

        TimeSpan gap = DateTime.Now - lastMessage.Value;
        string gapText = gap.TotalHours < 1  ? $"{(int)gap.TotalMinutes} minutes ago"
                        : gap.TotalHours < 24 ? $"{(int)gap.TotalHours} hours ago"
                        : $"{(int)gap.TotalDays} days ago";

        return Task.FromResult<ToolResult>(
            $"Last message from your owner: {lastMessage.Value:dddd, d MMMM yyyy — HH:mm} ({gapText}). " +
            $"Days with any conversation in the last 7: {activeDays.Count}/7.");
    }
}

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>Turns a message as the chat window stores it into text a person can read in a chat log. The window's
/// markers (<c>&lt;!--ari-…--&gt;</c>) become one line each, like "[tool call: read_file notes.txt]"; the ones that
/// only carry data for the window (file diffs, review passes, batch ends) are dropped.</summary>
internal static class ChatLogText
{
    private static readonly Regex Marker = new(@"<!--ari-(?<kind>[a-z]+(?:-[a-z]+)*)(?::(?<rest>.*?))?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    // A chat service message arrives as "[21/11/2026 14:35] [name via DM]: text" (or "[name [MASTER] in #general]").
    private static readonly Regex ServicePrefix = new(@"^\[\d{2}/\d{2}/\d{4} \d{2}:\d{2}\] \[(?<who>.+?)(?: \[MASTER\])? (?:via|in) [^\]]*\]: ?", RegexOptions.Multiline | RegexOptions.Compiled);

    private const char MARK = '\u0002';   // tags a line made from a marker until the tidy pass

    internal static string Readable(string text, string speaker)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        text = ServicePrefix.Replace(text, m =>
            string.Equals(m.Groups["who"].Value.Trim(), speaker.Trim(), StringComparison.OrdinalIgnoreCase) ? "" : $"{m.Groups["who"].Value.Trim()}: ");

        HashSet<string> started = new();
        text = Marker.Replace(text, m =>
        {
            string line = Describe(m.Groups["kind"].Value, m.Groups["rest"].Value, started);
            return line.Length == 0 ? "" : $"\n{MARK}{line}\n";
        });

        // Tidy: no trailing spaces, at most one blank line in a row, and none next to a marker line, so a run
        // of tool calls reads as one block.
        StringBuilder sb = new();
        bool blank = false, lastWasMark = false;
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) { blank = sb.Length > 0; continue; }
            bool mark = line[0] == MARK;
            if (mark) line = line[1..];
            if (sb.Length > 0) sb.Append(blank && !mark && !lastWasMark ? "\n\n" : "\n");
            sb.Append(line);
            blank = false; lastWasMark = mark;
        }
        return sb.ToString();
    }

    private static string Describe(string kind, string rest, HashSet<string> started)
    {
        switch (kind)
        {
            case "tool-start":
            case "tool-done":
            case "tool-end":
            {
                (string name, string label) = NameAndLabel(rest);
                string key = $"{name}\u0001{label}";
                // A call shows as start, then done/end with the same label: one line for the pair.
                if (kind != "tool-start" && started.Contains(key)) return "";
                started.Add(key);
                return label.Length == 0 ? $"[tool call: {name}]" : $"[tool call: {name} {label}]";
            }
            case "tool-error":
            {
                int a = rest.IndexOf(':');
                string name  = a < 0 ? rest : rest[..a];
                string after = a < 0 ? "" : rest[(a + 1)..];
                int b = after.IndexOf(':');
                string label = Clean(b < 0 ? after : after[..b]);
                string error = b < 0 ? "" : Clean(after[(b + 1)..]);
                return $"[tool error: {name}{(label.Length > 0 ? " " + label : "")}{(error.Length > 0 ? " — " + error : "")}]";
            }
            case "tool-mode":
                return $"[{Clean(NameAndLabel(rest).Label)}]";
            case "file":
                return $"[file sent: {Path.GetFileName(LastPart(rest))}]";
            case "image":
                return $"[image shown: {LastPart(rest)}]";
            case "visual":
                return $"[visual shown: {LastPart(rest)}]";
            case "browser":
                return $"[browser: {Clean(rest)}]";
            case "subthread":
            {
                int bar = rest.IndexOf('|');
                return $"[sub-agent: {Clean(bar < 0 ? rest : rest[(bar + 1)..])}]";
            }
            case "plan-proposed":
                return "[plan proposed]";
            case "persona-edit":
                return "[persona edit proposed]";
            default:
                return "";   // batch-end, edit and review carry data for the chat window only
        }
    }

    // "write_file:staff-summon.html|+353|-2|<diff>" → ("write_file", "staff-summon.html")
    private static (string Name, string Label) NameAndLabel(string rest)
    {
        int colon = rest.IndexOf(':');
        if (colon < 0) return (rest, "");
        string label = rest[(colon + 1)..];
        int bar = label.IndexOf('|');
        return (rest[..colon], Clean(bar < 0 ? label : label[..bar]));
    }

    private static string LastPart(string rest)
    {
        int colon = rest.LastIndexOf(':');
        return Clean(colon < 0 ? rest : rest[(colon + 1)..]);
    }

    private static string Clean(string s) =>
        WebUtility.HtmlDecode(s).Replace('∶', ':').Replace('¦', '|').Replace("\n", " ").Trim();
}

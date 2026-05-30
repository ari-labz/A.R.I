using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace ARI.Brain;

public static class MarkdownConverter
{
    /// <summary>Converts Engram markdown to HTML for Trilium storage.</summary>
    public static string ToHtml(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        bool inList = false;

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd();

            if (string.IsNullOrWhiteSpace(line))
            {
                CloseList(sb, ref inList);
                continue;
            }

            if (line.StartsWith("### ")) { CloseList(sb, ref inList); sb.AppendLine($"<h3>{Inline(line[4..])}</h3>"); continue; }
            if (line.StartsWith("## "))  { CloseList(sb, ref inList); sb.AppendLine($"<h2>{Inline(line[3..])}</h2>"); continue; }
            if (line.StartsWith("# "))   { CloseList(sb, ref inList); sb.AppendLine($"<h1>{Inline(line[2..])}</h1>"); continue; }

            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                if (!inList) { sb.AppendLine("<ul>"); inList = true; }
                sb.AppendLine($"<li>{Inline(line[2..])}</li>");
                continue;
            }

            CloseList(sb, ref inList);
            sb.AppendLine($"<p>{Inline(line)}</p>");
        }

        CloseList(sb, ref inList);
        return sb.ToString();
    }

    /// <summary>Converts stored Trilium HTML back to Engram markdown with [[Name]] links.</summary>
    public static string FromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // Inline elements first (before stripping tags)
        html = Regex.Replace(html, @"<a[^>]*>([^<]+)</a>", "[[$1]]", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<strong>(.*?)</strong>", "**$1**", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<b>(.*?)</b>",          "**$1**", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<em>(.*?)</em>",        "*$1*",   RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<i>(.*?)</i>",          "*$1*",   RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Block elements
        html = Regex.Replace(html, @"<h1[^>]*>(.*?)</h1>", "\n# $1\n",   RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<h2[^>]*>(.*?)</h2>", "\n## $1\n",  RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<h3[^>]*>(.*?)</h3>", "\n### $1\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<li[^>]*>(.*?)</li>",  "\n- $1",    RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"</?[uo]l[^>]*>",       "\n",        RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<br\s*/?>",             "\n",        RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<p[^>]*>(.*?)</p>",    "\n$1\n",    RegexOptions.Singleline | RegexOptions.IgnoreCase);

        html = Regex.Replace(html, @"<[^>]+>", "");
        html = HttpUtility.HtmlDecode(html);
        html = Regex.Replace(html, @"\n{3,}", "\n\n").Trim();
        return html;
    }

    /// <summary>Replaces {{LINK:Name}} placeholders with Trilium anchor tags.</summary>
    public static string ResolveLinks(string html, Dictionary<string, string> noteIds)
    {
        foreach (var (name, id) in noteIds)
        {
            string placeholder = $"{{{{LINK:{name}}}}}";
            string link = $"<a class=\"reference-link\" href=\"#root/{id}\">{HttpUtility.HtmlEncode(name)}</a>";
            html = html.Replace(placeholder, link, StringComparison.OrdinalIgnoreCase);
        }
        return html;
    }

    // ── Private ──────────────────────────────────────────────────────────────────

    private static void CloseList(StringBuilder sb, ref bool inList)
    {
        if (!inList) return;
        sb.AppendLine("</ul>");
        inList = false;
    }

    // Processes inline markdown within a single line: links, bold, italic.
    // [[Name]] links are extracted before HTML encoding so special chars in names survive.
    private static string Inline(string text)
    {
        // Extract [[Name]] links before encoding (prevents & etc. corrupting note names)
        var links = new List<string>();
        text = Regex.Replace(text, @"\[\[([^\]]+)\]\]", m =>
        {
            links.Add(m.Groups[1].Value);
            return $"\x00LINK{links.Count - 1}\x00";
        });

        text = HttpUtility.HtmlEncode(text);

        // Restore as {{LINK:Name}} placeholders
        for (int i = 0; i < links.Count; i++)
            text = text.Replace($"\x00LINK{i}\x00", $"{{{{LINK:{links[i]}}}}}");

        // Bold and italic
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        text = Regex.Replace(text, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "<em>$1</em>");

        return text;
    }
}

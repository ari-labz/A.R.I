using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ARI.LLM;

internal static class RedditSummariser
{
    internal static string Parse(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var sb = new StringBuilder();

        // ── Title ─────────────────────────────────────────────────────────────
        var titleNode = doc.DocumentNode.SelectSingleNode("//a[contains(@class,'title') and contains(@class,'may-blank')]")
                     ?? doc.DocumentNode.SelectSingleNode("//p[@class='title']//a[@class='title ']");
        string title = titleNode is not null ? CleanText(titleNode.InnerText) : "Untitled";
        sb.AppendLine($"**{title}**");

        // ── Post image (link posts with a preview thumbnail) ──────────────────
        var linkThing = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'thing') and contains(@class,'link')]");
        if (linkThing is not null)
        {
            // Full-size preview image embedded in the page
            var previewImg = linkThing.SelectSingleNode(".//a[contains(@class,'thumbnail')]//img");
            if (previewImg is not null)
            {
                string src = previewImg.GetAttributeValue("src", "")
                          ?? previewImg.GetAttributeValue("data-src", "");
                if (!string.IsNullOrWhiteSpace(src))
                {
                    if (src.StartsWith("//")) src = "https:" + src;
                    sb.AppendLine($"[image: {src}]");
                }
            }
        }

        // ── Post body (self-post text) ─────────────────────────────────────────
        var selftext = doc.DocumentNode
            .SelectSingleNode("//div[contains(@class,'thing') and contains(@class,'link')]//div[contains(@class,'usertext-body')]//div[@class='md']");
        if (selftext is not null)
        {
            string body = CleanText(selftext.InnerText);
            if (!string.IsNullOrWhiteSpace(body))
                sb.AppendLine().AppendLine(body);
        }

        // ── Comments ──────────────────────────────────────────────────────────
        var commentArea = doc.DocumentNode
            .SelectSingleNode("//div[contains(@class,'commentarea')]//div[contains(@class,'sitetable') and contains(@class,'nestedlisting')]");

        if (commentArea is not null)
        {
            sb.AppendLine().AppendLine("--- comments ---");
            RenderComments(commentArea, sb, depth: 0);
        }

        return sb.ToString().Trim();
    }

    private static void RenderComments(HtmlNode container, StringBuilder sb, int depth)
    {
        string prefix = string.Concat(Enumerable.Repeat("| ", depth));

        foreach (HtmlNode thing in container.ChildNodes)
        {
            if (thing.NodeType != HtmlNodeType.Element) continue;
            if (!thing.GetAttributeValue("data-type", "").Equals("comment", StringComparison.Ordinal)) continue;

            string author = thing.GetAttributeValue("data-author", "[deleted]");

            // Comment body is inside .entry .usertext-body .md
            var mdNode = thing.SelectSingleNode(".//div[contains(@class,'entry')]//div[contains(@class,'usertext-body')]//div[@class='md']");
            string body = mdNode is not null ? CleanText(mdNode.InnerText) : "[removed]";

            // Truncate very long comments — the LLM doesn't need the full essay
            if (body.Length > 600)
                body = body[..600] + "…";

            sb.AppendLine();
            sb.AppendLine($"{prefix}{author}:");
            foreach (string line in body.Split('\n'))
                sb.AppendLine($"{prefix}{line}");

            // Recurse into replies
            var childDiv  = thing.SelectSingleNode(".//div[@class='child']");
            var sitetable = childDiv?.SelectSingleNode(".//div[contains(@class,'sitetable')]");
            if (sitetable is not null)
                RenderComments(sitetable, sb, depth + 1);
        }
    }

    private static string CleanText(string raw)
    {
        // Decode HTML entities, collapse whitespace
        string decoded = System.Net.WebUtility.HtmlDecode(raw);
        decoded = Regex.Replace(decoded, @"[ \t]+", " ");
        decoded = Regex.Replace(decoded, @"\n{3,}", "\n\n");
        return decoded.Trim();
    }
}

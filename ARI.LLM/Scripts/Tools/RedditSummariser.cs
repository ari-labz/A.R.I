using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ARI.LLM;

internal static class RedditSummariser
{
    internal static string Parse(string html)
    {
        HtmlDocument htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(html);

        StringBuilder output = new StringBuilder();

        HtmlNode? titleNode = htmlDoc.DocumentNode.SelectSingleNode("//a[contains(@class,'title') and contains(@class,'may-blank')]")
                           ?? htmlDoc.DocumentNode.SelectSingleNode("//p[@class='title']//a[@class='title ']");
        string title = titleNode is not null ? CleanText(titleNode.InnerText) : "Untitled";
        output.AppendLine($"**{title}**");

        HtmlNode? linkThing = htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class,'thing') and contains(@class,'link')]");
        if (linkThing is not null)
        {
            HtmlNode? previewImg = linkThing.SelectSingleNode(".//a[contains(@class,'thumbnail')]//img");
            if (previewImg is not null)
            {
                string src = previewImg.GetAttributeValue("src", "")
                          ?? previewImg.GetAttributeValue("data-src", "");
                if (!string.IsNullOrWhiteSpace(src))
                {
                    if (src.StartsWith("//")) src = "https:" + src;
                    output.AppendLine($"[image: {src}]");
                }
            }
        }

        // ── Post body (self-post text) ─────────────────────────────────────────
        HtmlNode? selftext = htmlDoc.DocumentNode
            .SelectSingleNode("//div[contains(@class,'thing') and contains(@class,'link')]//div[contains(@class,'usertext-body')]//div[@class='md']");
        if (selftext is not null)
        {
            string body = CleanText(selftext.InnerText);
            if (!string.IsNullOrWhiteSpace(body))
                output.AppendLine().AppendLine(body);
        }

        HtmlNode? commentArea = htmlDoc.DocumentNode
            .SelectSingleNode("//div[contains(@class,'commentarea')]//div[contains(@class,'sitetable') and contains(@class,'nestedlisting')]");

        if (commentArea is not null)
        {
            output.AppendLine().AppendLine("--- comments ---");
            RenderComments(commentArea, output, depth: 0);
        }

        return output.ToString().Trim();
    }

    private static void RenderComments(HtmlNode container, StringBuilder builder, int depth)
    {
        StringBuilder prefixBuilder = new StringBuilder();
        for (int level = 0; level < depth; level++) prefixBuilder.Append("| ");
        string prefix = prefixBuilder.ToString();

        foreach (HtmlNode thing in container.ChildNodes)
        {
            if (thing.NodeType != HtmlNodeType.Element) continue;
            if (!thing.GetAttributeValue("data-type", "").Equals("comment", StringComparison.Ordinal)) continue;

            string author = thing.GetAttributeValue("data-author", "[deleted]");

            HtmlNode? mdNode = thing.SelectSingleNode(".//div[contains(@class,'entry')]//div[contains(@class,'usertext-body')]//div[@class='md']");
            string body = mdNode is not null ? CleanText(mdNode.InnerText) : "[removed]";

            if (body.Length > 600)
                body = body[..600] + "…";

            builder.AppendLine();
            builder.AppendLine($"{prefix}{author}:");
            foreach (string line in body.Split('\n'))
                builder.AppendLine($"{prefix}{line}");

            HtmlNode? childDiv  = thing.SelectSingleNode(".//div[@class='child']");
            HtmlNode? sitetable = childDiv?.SelectSingleNode(".//div[contains(@class,'sitetable')]");
            if (sitetable is not null)
                RenderComments(sitetable, builder, depth + 1);
        }
    }

    private static string CleanText(string raw)
    {
        string decoded = WebUtility.HtmlDecode(raw);
        decoded = Regex.Replace(decoded, @"[ \t]+", " ");
        decoded = Regex.Replace(decoded, @"\n{3,}", "\n\n");
        return decoded.Trim();
    }
}

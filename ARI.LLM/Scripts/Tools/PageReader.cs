using HtmlAgilityPack;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ARI.LLM;

// Extracts readable text from HTML locally. Prefers <article>/<main>/<[role=main]>, falls back
// to <body> after stripping nav/header/footer/aside/script/style nodes.
internal static class PageReader
{
    internal const int MIN_MEANINGFUL_CHARS = 200;

    internal static string Extract(string html, string sourceUrl)
    {
        HtmlDocument htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(html);

        RemoveNoise(htmlDoc.DocumentNode);

        HtmlNode? content = FindMainContent(htmlDoc.DocumentNode)
                         ?? htmlDoc.DocumentNode.SelectSingleNode("//body");

        if (content is null) return "";

        string text = CollapseWhitespace(WebUtility.HtmlDecode(content.InnerText));
        if (text.Length > 0)
        {
            string pageTitle = ExtractTitle(htmlDoc.DocumentNode);
            if (pageTitle.Length > 0) text = $"Title: {pageTitle}\n\n{text}";
        }

        return text;
    }

    private static void RemoveNoise(HtmlNode root)
    {
        string[] noiseSelectors =
        [
            "//script", "//style", "//noscript", "//iframe",
            "//nav", "//header", "//footer", "//aside",
            "//*[@role='navigation']", "//*[@role='banner']", "//*[@role='contentinfo']",
            "//*[contains(@class,'cookie')]", "//*[contains(@class,'popup')]",
            "//*[contains(@class,'ad-')]", "//*[contains(@id,'sidebar')]",
        ];

        List<HtmlNode> toRemove = new List<HtmlNode>();
        foreach (string selector in noiseSelectors)
        {
            HtmlNodeCollection? nodes = root.SelectNodes(selector);
            if (nodes is not null)
                foreach (HtmlNode node in nodes)
                    toRemove.Add(node);
        }

        foreach (HtmlNode node in toRemove)
            node.Remove();
    }

    private static HtmlNode? FindMainContent(HtmlNode root)
    {
        string[] candidates =
        [
            "//article",
            "//main",
            "//*[@role='main']",
            "//*[contains(@class,'content')]",
            "//*[contains(@class,'post-body')]",
            "//*[contains(@class,'entry-content')]",
            "//*[contains(@id,'content')]",
        ];

        foreach (string selector in candidates)
        {
            HtmlNode? node = root.SelectSingleNode(selector);
            if (node is not null && node.InnerText.Trim().Length > MIN_MEANINGFUL_CHARS)
                return node;
        }

        return null;
    }

    private static string ExtractTitle(HtmlNode root)
    {
        HtmlNode? titleNode = root.SelectSingleNode("//title")
                           ?? root.SelectSingleNode("//h1");
        return titleNode is not null ? CollapseWhitespace(WebUtility.HtmlDecode(titleNode.InnerText)) : "";
    }

    private static string CollapseWhitespace(string text)
    {
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}

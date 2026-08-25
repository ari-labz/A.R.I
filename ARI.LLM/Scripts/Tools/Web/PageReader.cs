using SmartReader;

namespace ARI.LLM;

// Extracts readable text from HTML using Mozilla Readability (via SmartReader).
internal static class PageReader
{
    internal const int MIN_MEANINGFUL_CHARS = 200;

    internal static string Extract(string html, string sourceUrl)
    {
        Reader reader = new Reader(sourceUrl, html);
        Article article = reader.GetArticle();

        if (!article.IsReadable) return "";

        string body  = article.TextContent?.Trim() ?? "";
        string title = article.Title?.Trim() ?? "";

        if (body.Length == 0) return "";
        return title.Length > 0 ? $"Title: {title}\n\n{body}" : body;
    }
}

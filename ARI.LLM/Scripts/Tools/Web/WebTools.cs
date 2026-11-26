using ARI.Common;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ARI.LLM;

internal sealed class FetchPage : Tool
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect      = true,
    }) { Timeout = TimeSpan.FromSeconds(20) };

    internal override string     Name   => "fetch_page";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "fetch_page",
            description = "Fetch a web page and return its content as readable text. " +
                          "Use this after search_web to read the full content of a result.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    url = new
                    {
                        type        = "string",
                        description = "The URL to fetch."
                    }
                },
                required = new[] { "url" }
            }
        }
    };

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument displayDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args);
            string pageUrl = displayDoc.RootElement.TryGetProperty("url", out JsonElement urlEl) ? urlEl.GetString() ?? "" : "";
            return new Browsing { Url = pageUrl }.Render();
        }
        catch { return new Browsing().Render(); }
    };

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        JsonElement args;
        try
        {
            using JsonDocument argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            args = argsDoc.RootElement.Clone();
        }
        catch { return "Error: could not parse arguments."; }

        string url = args.TryGetProperty("url", out JsonElement urlElement) ? urlElement.GetString() ?? "" : "";
        if (url.Length == 0) return "Error: 'url' is required.";

        return IsRedditUrl(url)
            ? await FetchReddit(url)
            : await FetchPageContent(url);
    }

    private static async Task<string> FetchReddit(string url)
    {
        // Rewrite to old.reddit.com — plain HTML, no JS required, no login wall.
        url = ToOldReddit(url);

        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            req.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            using HttpResponseMessage resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return $"Failed to fetch Reddit page (HTTP {(int)resp.StatusCode}).";

            string html = await resp.Content.ReadAsStringAsync();
            string text = RedditSummariser.Parse(html);

            const int maxChars = 12000;
            if (text.Length > maxChars)
                text = text[..maxChars] + "\n\n[content truncated]";

            return text.Trim().Length > 0 ? text.Trim() : "Page returned no readable content.";
        }
        catch (Exception ex)
        {
            return $"Failed to fetch Reddit page: {ex.Message}";
        }
    }

    private static async Task<string> FetchPageContent(string url)
    {
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.Add("Accept", "text/html,application/xhtml+xml,*/*;q=0.8");
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            req.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            using HttpResponseMessage resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return $"Failed to fetch page (HTTP {(int)resp.StatusCode}).";

            string html    = await resp.Content.ReadAsStringAsync();
            string content = PageReader.Extract(html, url);

            if (content.Length < PageReader.MIN_MEANINGFUL_CHARS)
            {
                // A short, plain page (a notice, a price list) has nothing the article extractor recognises; read its visible words instead.
                string plain = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(
                    System.Text.RegularExpressions.Regex.Replace(html, @"(?is)<(script|style|noscript|head)\b.*?</\1>|<!--.*?-->", " "), @"(?s)<[^>]+>", " "));
                plain = System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", " ").Trim();
                if (plain.Length >= 15) return plain.Length > 4000 ? plain[..4000] + "…" : plain;
                return $"Page returned very little text ({content.Length} chars) — it probably needs JavaScript to render. Open it with the browser tool instead.";
            }

            const int MAX_CHARS = 12000;
            if (content.Length > MAX_CHARS)
                content = content[..MAX_CHARS] + "\n\n[content truncated]";

            return content;
        }
        catch (Exception ex)
        {
            return $"Failed to fetch page: {ex.Message}";
        }
    }

    private static bool IsRedditUrl(string url)
        => url.Contains("reddit.com/", StringComparison.OrdinalIgnoreCase);

    private static string ToOldReddit(string url)
    {
        if (url.Contains("://old.reddit.com/", StringComparison.OrdinalIgnoreCase)) return url;
        if (url.Contains("://www.reddit.com/", StringComparison.OrdinalIgnoreCase))
            return url.Replace("://www.reddit.com/", "://old.reddit.com/", StringComparison.OrdinalIgnoreCase);
        if (url.Contains("://reddit.com/", StringComparison.OrdinalIgnoreCase))
            return url.Replace("://reddit.com/", "://old.reddit.com/", StringComparison.OrdinalIgnoreCase);
        return url;
    }

}

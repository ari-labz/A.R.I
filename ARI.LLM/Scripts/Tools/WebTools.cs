using System.Net;
using System.Text;
using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// Searches the web via the local SearXNG instance and returns ranked results.
/// </summary>
internal sealed class SearchWeb : Tool
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    }) { Timeout = TimeSpan.FromSeconds(15) };

    internal override string Name => "search_web";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "search_web",
            description = "Search the web and return ranked results with titles, URLs, and snippets. " +
                          "Append site:reddit.com to find community discussions. " +
                          "Results are current — use this for anything time-sensitive or that you're unsure about.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    query = new
                    {
                        type        = "string",
                        description = "The search query. Supports operators like site:reddit.com, filetype:pdf, etc."
                    },
                    num_results = new
                    {
                        type        = "integer",
                        description = "Number of results to return (1–10). Defaults to 5.",
                    }
                },
                required = new[] { "query" }
            }
        }
    };

    internal override async Task<string> Execute(string argsJson)
    {
        JsonElement args;
        try { args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement; }
        catch { return "Error: could not parse arguments."; }

        string query = args.TryGetProperty("query", out JsonElement q) ? q.GetString() ?? "" : "";
        if (query.Length == 0) return "Error: 'query' is required.";

        int numResults = args.TryGetProperty("num_results", out JsonElement nr) && nr.ValueKind == JsonValueKind.Number
            ? Math.Clamp(nr.GetInt32(), 1, 10)
            : 5;

        string url = $"http://localhost:{Dependency.SearXngPort}/search" +
                     $"?q={Uri.EscapeDataString(query)}&format=json&pageno=1";

        string json;
        try { json = await Http.GetStringAsync(url); }
        catch (Exception ex)
        {
            return $"Search unavailable: {ex.Message}. SearXNG may still be starting — try again in a moment.";
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out JsonElement results))
            return "No results returned.";

        var sb = new StringBuilder();
        int count = 0;
        foreach (JsonElement result in results.EnumerateArray())
        {
            if (count >= numResults) break;
            string title   = result.TryGetProperty("title",   out JsonElement t) ? t.GetString() ?? "" : "";
            string resultUrl = result.TryGetProperty("url",   out JsonElement u) ? u.GetString() ?? "" : "";
            string snippet = result.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";

            sb.AppendLine($"[{count + 1}] {title}");
            sb.AppendLine($"    URL: {resultUrl}");
            if (snippet.Length > 0)
                sb.AppendLine($"    {snippet}");
            sb.AppendLine();
            count++;
        }

        return count == 0 ? "No results found." : sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Fetches a web page and returns its content as clean text via Jina Reader.
/// Automatically rewrites reddit.com URLs to old.reddit.com for reliable access.
/// </summary>
internal sealed class FetchPage : Tool
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect      = true,
    }) { Timeout = TimeSpan.FromSeconds(20) };

    private const string JinaPrefix = "https://r.jina.ai/";

    internal override string Name => "fetch_page";
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

    internal override async Task<string> Execute(string argsJson)
    {
        JsonElement args;
        try { args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement; }
        catch { return "Error: could not parse arguments."; }

        string url = args.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? "" : "";
        if (url.Length == 0) return "Error: 'url' is required.";

        return IsRedditUrl(url)
            ? await FetchReddit(url)
            : await FetchViaJina(url);
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
            string text = StripHtml(html);

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

    private static async Task<string> FetchViaJina(string url)
    {
        string fetchUrl = JinaPrefix + url;

        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, fetchUrl);
            req.Headers.Add("Accept", "text/plain");
            req.Headers.Add("User-Agent", "ARI/1.0");
            req.Headers.Add("X-Return-Format", "text");

            using HttpResponseMessage resp = await Http.SendAsync(req);
            string content = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return $"Failed to fetch page (HTTP {(int)resp.StatusCode}).";

            const int maxChars = 12000;
            if (content.Length > maxChars)
                content = content[..maxChars] + "\n\n[content truncated]";

            return content.Trim();
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

    // Minimal HTML stripper — pulls readable text out of old.reddit's plain HTML.
    private static string StripHtml(string html)
    {
        // Remove script/style blocks entirely.
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<(script|style)[^>]*>.*?</(script|style)>", "", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Strip remaining tags.
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", " ");
        // Collapse whitespace.
        html = System.Text.RegularExpressions.Regex.Replace(html, @"\s{2,}", "\n");
        return System.Net.WebUtility.HtmlDecode(html);
    }
}

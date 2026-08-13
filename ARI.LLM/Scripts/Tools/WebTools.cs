using ARI.Common;
using Microsoft.Extensions.Logging;
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

    // Results whose text overlaps the query in no way at all are dropped, and a search that keeps
    // nothing says so in these words. Agent matches on this prefix so a search that told the model
    // nothing does not burn its search budget.
    internal const string NoRelevantPrefix = "No relevant results for";

    internal override string Name => "search_web";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "search_web",
            description = "Search the web and return ranked results with titles, URLs, and snippets. " +
                          "Results are current — use this for anything time-sensitive or that you're unsure about. " +
                          "Search the name of a thing exactly as it is written, and prefer its canonical identifier: " +
                          "'Qwen3.6-35B-A3B' finds the model, 'Qwen 3.6 35B A3B model' finds nothing. " +
                          "Keep queries short, one subject at a time, and leave out filler words like 'model', " +
                          "'vs' or 'comparison' — they make a working query fail. " +
                          "Append site:reddit.com for community discussion and opinion.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    query = new
                    {
                        type        = "string",
                        description = "The search query. Short and literal beats descriptive. Supports operators like site:reddit.com, filetype:pdf, etc."
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

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument displayDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args);
            string searchQuery = displayDoc.RootElement.TryGetProperty("query", out JsonElement queryEl) ? queryEl.GetString() ?? "" : "";
            return new WebSearching { Query = searchQuery }.Render();
        }
        catch { return new WebSearching().Render(); }
    };

    internal override async Task<string> Execute(string argsJson)
    {
        JsonElement args;
        try
        {
            using JsonDocument argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            args = argsDoc.RootElement.Clone();
        }
        catch { return "Error: could not parse arguments."; }

        string query = args.TryGetProperty("query", out JsonElement queryEl) ? queryEl.GetString() ?? "" : "";
        if (query.Length == 0) return "Error: 'query' is required.";

        int numResults = args.TryGetProperty("num_results", out JsonElement nr) && nr.ValueKind == JsonValueKind.Number
            ? Math.Clamp(nr.GetInt32(), 1, 10)
            : 5;

        string? searXngStatus = Dependency.SearXngStatus;
        if (searXngStatus is null)
            return "Web search is not available yet — SearXNG is still starting. Try again in a moment.";
        if (searXngStatus.Length > 0)
            return $"Web search is not available: {searXngStatus}";

        string searchUrl = $"http://localhost:{Dependency.SEARXNG_PORT}/search" +
                           $"?q={Uri.EscapeDataString(query)}&format=json&pageno=1";

        string json;
        try { json = await Http.GetStringAsync(searchUrl); }
        catch (Exception ex)
        {
            return $"Search unavailable: {ex.Message}.";
        }

        using JsonDocument responseDoc = JsonDocument.Parse(json);
        if (!responseDoc.RootElement.TryGetProperty("results", out JsonElement results))
            return "No results returned.";

        string engineHealth = ReportEngineHealth(responseDoc.RootElement, results);

        // Relevance gate. When an upstream engine has no match for a query it does not return an empty
        // set — it serves a page of unrelated filler, which SearXNG scrapes as if it were results (a
        // search for a real model returned Hotmail help and Swedish salary listings). Anything sharing
        // no word at all with the query is that filler, so it is dropped rather than shown as evidence.
        List<string> queryTerms = DistinctiveTerms(query);
        List<(string Title, string Url, string Snippet)> kept = new List<(string Title, string Url, string Snippet)>();
        int dropped = 0;

        foreach (JsonElement result in results.EnumerateArray())
        {
            string title     = result.TryGetProperty("title",   out JsonElement titleEl)   ? titleEl.GetString()   ?? "" : "";
            string resultUrl = result.TryGetProperty("url",     out JsonElement urlEl)     ? urlEl.GetString()     ?? "" : "";
            string snippet   = result.TryGetProperty("content", out JsonElement contentEl) ? contentEl.GetString() ?? "" : "";

            if (queryTerms.Count > 0 && !IsRelevant(queryTerms, title, resultUrl, snippet)) { dropped++; continue; }
            if (kept.Count < numResults) kept.Add((title, resultUrl, snippet));
        }

        if (kept.Count == 0)
        {
            StringBuilder none = new StringBuilder();
            none.AppendLine($"{NoRelevantPrefix} \"{query}\".");
            if (dropped > 0)
                none.AppendLine($"{dropped} result(s) came back but none mentioned the search terms at all — that is the " +
                                 "search engine padding an empty result set, not evidence about the subject.");
            none.AppendLine();
            none.AppendLine("Do NOT rewrite this query and search again — rephrasing an unindexed term returns the same filler. " +
                            "Do NOT conclude the term is a typo, misremembered, or non-existent: a search finding nothing is a " +
                            "fact about the index, not about the world, and the term is very likely newer than your training data. " +
                            "Either search the exact canonical identifier if you have not yet (e.g. 'Qwen3.6-35B-A3B' rather than " +
                            "'Qwen 3.6 35B A3B'), or tell the user plainly that your search found nothing and ask them for a source.");
            if (engineHealth.Length > 0) { none.AppendLine(); none.Append(engineHealth); }
            return none.ToString().TrimEnd();
        }

        StringBuilder resultsBuilder = new StringBuilder();
        for (int i = 0; i < kept.Count; i++)
        {
            resultsBuilder.AppendLine($"[{i + 1}] {kept[i].Title}");
            resultsBuilder.AppendLine($"    URL: {kept[i].Url}");
            if (kept[i].Snippet.Length > 0) resultsBuilder.AppendLine($"    {kept[i].Snippet}");
            resultsBuilder.AppendLine();
        }
        if (dropped > 0)
            resultsBuilder.AppendLine($"({dropped} unrelated result(s) hidden — they matched none of the search terms.)");
        if (engineHealth.Length > 0) resultsBuilder.Append(engineHealth);

        return resultsBuilder.ToString().TrimEnd();
    }

    // Words carrying no identifying power. A query is judged on what is left after these are removed,
    // so "Qwen3.6 model" is judged on "qwen3.6" alone and a page about Qwen still counts as relevant.
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","and","are","about","at","best","between","by","can","community","compare","comparison",
        "difference","differences","do","does","for","from","how","in","is","it","latest","new","news","of",
        "on","or","other","review","reviews","sentiment","should","site","that","the","their","this","to",
        "up","use","using","versus","vs","what","when","where","which","who","why","with","model","models",
        "release","released","version","reddit","com","org","net","www","http","https",
    };

    /// <summary>Query words that actually identify the subject — operators, punctuation and filler removed.</summary>
    private static List<string> DistinctiveTerms(string query)
    {
        List<string> terms = new List<string>();
        foreach (string raw in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string term = raw.Trim('"', '\'', '(', ')', ',', '.', '?', '!', ':', ';');
            if (term.Contains(':')) continue;                       // site:, filetype: and friends
            if (term.Length < 2 || Filler.Contains(term)) continue;
            if (!term.Any(char.IsLetterOrDigit)) continue;
            terms.Add(term);

            // A canonical identifier is one token but several ideas ("Qwen3.6-35B-A3B"). Accept its parts
            // too, so a page writing the same thing with spaces still counts as a match. Matching is
            // any-of, so this only ever keeps more — it cannot cause a relevant result to be dropped.
            if (term.Contains('-') || term.Contains('/'))
                foreach (string part in term.Split('-', '/', StringSplitOptions.RemoveEmptyEntries))
                    if (part.Length >= 2 && !Filler.Contains(part)) terms.Add(part);
        }
        return terms;
    }

    /// <summary>True if the result mentions any identifying query term. Deliberately lenient — one hit is
    /// enough — so only results with nothing whatsoever in common with the query are removed.</summary>
    private static bool IsRelevant(List<string> terms, string title, string url, string snippet)
    {
        string haystack = $"{title} {url} {snippet}";
        return terms.Any(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    // Engine suspensions last hours (rate limit) to a day (CAPTCHA), and a search runs every few minutes,
    // so logging per call would write the same line hundreds of times. Log on change, or twice an hour.
    private static readonly object     DegradeLock = new();
    private static          string     lastDegradeState = "";
    private static          DateTime   lastDegradeLog   = DateTime.MinValue;
    private static readonly TimeSpan   DegradeLogCooldown = TimeSpan.FromMinutes(30);

    /// <summary>Reads SearXNG's unresponsive_engines, warns in the log when engines are blocked, and
    /// returns a note for the model so a thin result set reads as a broken tool rather than an empty world.</summary>
    private static string ReportEngineHealth(JsonElement root, JsonElement results)
    {
        if (!root.TryGetProperty("unresponsive_engines", out JsonElement down) || down.ValueKind != JsonValueKind.Array)
            return "";

        List<string> blocked = new List<string>();
        foreach (JsonElement entry in down.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() == 0) continue;
            string engine = entry[0].GetString() ?? "";
            string reason = entry.GetArrayLength() > 1 ? entry[1].GetString() ?? "" : "";
            if (engine.Length > 0) blocked.Add(reason.Length > 0 ? $"{engine} ({reason})" : engine);
        }
        if (blocked.Count == 0) return "";

        HashSet<string> workingEngines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement resultEntry in results.EnumerateArray())
            if (resultEntry.TryGetProperty("engine", out JsonElement engineEl) && engineEl.GetString() is { Length: > 0 } engineName)
                workingEngines.Add(engineName);

        int total = workingEngines.Count + blocked.Count;

        string state = string.Join(",", blocked);
        bool shouldLog;
        lock (DegradeLock)
        {
            shouldLog = state != lastDegradeState || DateTime.UtcNow - lastDegradeLog > DegradeLogCooldown;
            if (shouldLog) { lastDegradeState = state; lastDegradeLog = DateTime.UtcNow; }
        }
        if (shouldLog)
            Shared.Logger.LogWarning("[WebSearch] {Down} of {Total} search engines rate limited: {Engines}",
                blocked.Count, total, string.Join(", ", blocked));

        return $"[Search degraded: {blocked.Count} of {total} engines are rate limited or CAPTCHA'd " +
               $"({string.Join(", ", blocked)}). Results are thinner and lower quality than normal. Treat a missing " +
               "result as this tool failing, not as proof something does not exist — and tell the user your search " +
               "is rate limited so they know why the answer is thin.]";
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

    private const string JINA_PREFIX = "https://r.jina.ai/";

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

    internal override async Task<string> Execute(string argsJson)
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

    private static async Task<string> FetchViaJina(string url)
    {
        string fetchUrl = JINA_PREFIX + url;

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

}

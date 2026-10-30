using System.Text;

namespace ARI.LLM;

[Tool("http_request", "Make an HTTP request and read the response: call a web API, fetch a file or JSON, post data, or check that a service is up. " +
                      "For reading a normal web page use fetch_page, which cleans it up; use this when you need the raw response, a different method, headers or a body.")]
public class HttpRequest : Tool
{
    [Param("The full address, starting with http:// or https://")]
    public string Url;

    [Param("GET, POST, PUT, PATCH, DELETE or HEAD. Default GET.", Options = "GET,POST,PUT,PATCH,DELETE,HEAD", Optional = true)]
    public string Method = "GET";

    [Param("Request headers, one per line as 'Name: value'.", Optional = true)]
    public string Headers;

    [Param("The request body, for POST, PUT and PATCH. For JSON, write the JSON and add the header 'Content-Type: application/json'.", Optional = true)]
    public string Body;

    [Param("Give up after this many seconds. Default 30.", Optional = true)]
    public int TimeoutSeconds = 30;

    public override string OnValidate()
    {
        if (!IsAdmin) return "Only the owner, chatting in the app, can make arbitrary web requests.";
        return Uri.TryCreate(Url, UriKind.Absolute, out Uri u) && u.Scheme is "http" or "https" ? null : "The address must start with http:// or https://.";
    }

    public override async Task<string> Run()
    {
        using HttpRequestMessage req = new(new HttpMethod(Method.ToUpperInvariant()), Url);
        foreach (string line in (Headers ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = line.IndexOf(':');
            if (i <= 0) continue;
            string name = line[..i].Trim(), value = line[(i + 1)..].Trim();
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;   // belongs to the body
            req.Headers.TryAddWithoutValidation(name, value);
        }
        if (!string.IsNullOrEmpty(Body))
        {
            string ct = (Headers ?? "").Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Select(p => p[1].Trim()).FirstOrDefault() ?? "text/plain";
            req.Content = new StringContent(Body, Encoding.UTF8, ct);
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(Cancelled);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 300)));
        using HttpResponseMessage res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        string type = res.Content.Headers.ContentType?.MediaType ?? "";
        StringBuilder sb = new();
        sb.AppendLine($"HTTP {(int)res.StatusCode} {res.ReasonPhrase}");
        if (type.Length > 0) sb.AppendLine($"Content-Type: {type}");
        if (res.Content.Headers.ContentLength is { } len) sb.AppendLine($"Content-Length: {len}");
        if (res.Headers.Location is { } loc) sb.AppendLine($"Location: {loc}");
        sb.AppendLine();

        bool text = type.Length == 0 || type.StartsWith("text/") || type.Contains("json") || type.Contains("xml") || type.Contains("javascript") || type.Contains("yaml") || type.Contains("html");
        if (!text) { sb.Append($"(binary content, {type}. Use download_file to save it if you need the file.)"); return sb.ToString(); }

        byte[] bytes = await res.Content.ReadAsByteArrayAsync(cts.Token);
        string body = Encoding.UTF8.GetString(bytes);
        sb.Append(body.Length > 20_000 ? body[..20_000] + $"\n… [{body.Length - 20_000:N0} more characters not shown]" : body);
        return sb.ToString();
    }
}

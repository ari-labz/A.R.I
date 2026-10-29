using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.Common;

namespace ARI.LLM;

/// <summary>Draws something in the chat itself: a chart, diagram, table, small calculator or any page of HTML, shown under the message
/// in a sandboxed frame. The page is also kept as an .html file in the working folder so the person can download it.</summary>
internal sealed class ShowVisual : Tool
{
    private readonly Thread thread;
    internal ShowVisual(Thread thread) => this.thread = thread;

    internal override string     Name   => "show_visual";
    internal override ToolAccess Access => ToolAccess.Write;

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "show_visual",
            description = "Show a visual right in the chat: a chart, diagram, table, timeline, calculator, small interactive page or illustration. You write it as HTML (with CSS, and JavaScript if it needs to be interactive) and it appears under your message. " +
                          "Use it when a picture or interactive thing explains something better than words. Keep it self-contained: put the CSS and JavaScript inline; the only outside code allowed is from cdnjs.cloudflare.com and cdn.jsdelivr.net (e.g. Chart.js, d3), " +
                          "and the page cannot make network requests. Use a transparent background and the default font; dark text on light and light text on dark both work if you use currentColor. " +
                          "Give it sensible proportions (it is about 700px wide, and grows to fit its content). Draw SVG for diagrams. Chart.js draws into a <canvas>, not a div. The page is test-run first and not shown if it raises errors; you'll be told what failed. For a chart that goes into a document use make_chart instead. After showing it, say in a sentence what it shows; don't describe it in detail.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    title = new { type = "string", description = "A short name for the visual, e.g. Revenue by month." },
                    html  = new { type = "string", description = "The HTML to show: a fragment or a whole page, with inline CSS and JavaScript." },
                },
                required = new[] { "title", "html" }
            }
        }
    };

    private static string Slug(string title)
    {
        string s = Regex.Replace(title.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return (s.Length == 0 ? "visual" : s[..Math.Min(s.Length, 50)]) + ".html";
    }

    private static (string Title, string Html) Parse(string argsJson)
    {
        using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        string Get(string k) => doc.RootElement.TryGetProperty(k, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        return (Get("title"), Get("html"));
    }

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        if (thread.Medium == ThreadMedium.Discord) return (ToolResult)("This conversation is on Discord, which can't show a live visual. Describe it in words, or save it with write_file and send it with deliver_file.");
        (string title, string html) = Parse(argsJson);
        if (html.Trim().Length < 10) return (ToolResult)("[Error: give the HTML to show in html.]");
        if (Encoding.UTF8.GetByteCount(html) > 600_000) return (ToolResult)("[Error: that page is over 600 KB. Make it smaller.]");
        List<string> problems = new();
        try { problems = await BrowserHost.CheckHtml(html); } catch { /* no browser available: show it unchecked */ }
        if (problems.Count > 0)
            return $"[Not shown: the page has problems: {string.Join("; ", problems)}. Fix them and call show_visual again. (Chart.js needs a <canvas> element; check ids and that every library comes from cdnjs.cloudflare.com or cdn.jsdelivr.net.)]";
        string folder = thread.FilesystemRoot is { Length: > 0 } r ? r : Paths.ScratchpadDir(thread.Key);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, Slug(title));
        File.WriteAllText(file, html);
        thread.CreatedFiles.Add(Path.GetFullPath(file));
        return (ToolResult)($"Shown to the user under your message as \"{title}\". They can see it and use it now. Say in a sentence what it shows; don't repeat its contents. To change it, call show_visual again with the same title. It is also saved as {Path.GetFileName(file)}.");
    }

    // The marker the chat window turns into the frame; the file name comes from the title, so it needs no state.
    internal override Func<string, string>? DisplayAfter => args =>
    {
        try { (string title, string html) = Parse(args); return html.Trim().Length < 10 || thread.Medium == ThreadMedium.Discord ? "" : $"\n<!--ari-visual:{thread.Key}:{Slug(title)}-->"; }
        catch { return ""; }
    };
}

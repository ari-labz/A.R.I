using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ARI.Common;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace ARI.LLM;

/// <summary>One headless browser for the whole server, with a page per conversation. Chrome or Chromium is found on the machine; if
/// there isn't one it's downloaded once into ARI's data folder (about 150 MB), the same way ARI fetches its other dependencies.</summary>
internal static class BrowserHost
{
    private static IBrowser? browser;
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static readonly ConcurrentDictionary<string, IPage> pages = new();

    /// <summary>The browser to use, or null for the Chromium ARI downloads. By default Ari has her own: a Chromium of exactly the version the
    /// automation library was built for, fetched once into ARI's data folder, so a Chrome update on this machine can't break her. Set ARI_CHROME
    /// to the path of an installed Chrome or Chromium to use that instead.</summary>
    internal static string? ExecutablePath()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("ARI_CHROME");
        return !string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv) ? fromEnv : null;
    }

    // Chrome installed on this machine: a last resort when ARI's own Chromium can't be fetched. A Chrome newer than the automation library
    // knows can misbehave, which is why it isn't the default.
    private static string? InstalledChrome() => new[]
    {
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/Applications/Chromium.app/Contents/MacOS/Chromium",
        "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/snap/bin/chromium",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    }.FirstOrDefault(File.Exists);

    private static async Task<string> FindBrowser()
    {
        if (ExecutablePath() is { } chosen) return chosen;
        BrowserFetcher fetcher = new(new BrowserFetcherOptions { Path = Path.Combine(Paths.AppData, "Browser") });
        // Already fetched on an earlier run?
        foreach (var have in fetcher.GetInstalledBrowsers()) if (File.Exists(have.GetExecutablePath())) return have.GetExecutablePath();
        try
        {
            Shared.Logger.LogInformation("[Browser] Getting ARI's own Chromium (one time: about 150 MB to download, about 550 MB on disk)…");
            string exe = (await fetcher.DownloadAsync()).GetExecutablePath();
            Shared.Logger.LogInformation("[Browser] Chromium ready at {Path}.", exe);
            return exe;
        }
        catch (Exception e)
        {
            Shared.Logger.LogWarning(e, "[Browser] Couldn't download Chromium.");
            if (InstalledChrome() is { } fallback) { Shared.Logger.LogInformation("[Browser] Using the Chrome installed at {Path} instead.", fallback); return fallback; }
            throw new InvalidOperationException("The browser isn't set up yet: ARI couldn't download its own Chromium (check the internet connection and try again) and there's no Chrome on this machine to use instead.");
        }
    }

    private static async Task<IBrowser> Launch()
    {
        if (browser is { IsConnected: true }) return browser;
        string exe = await FindBrowser();
        browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true, ExecutablePath = exe,
            Args = new[] { "--no-sandbox", "--disable-dev-shm-usage", "--hide-scrollbars", "--window-size=1280,900" },
            DefaultViewport = new ViewPortOptions { Width = 1280, Height = 900 },
        });
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { browser?.CloseAsync().Wait(2000); } catch { } };
        return browser;
    }

    internal static async Task<IPage> PageFor(string conversation, bool create)
    {
        await gate.WaitAsync();
        try
        {
            if (pages.TryGetValue(conversation, out IPage? existing) && !existing.IsClosed) return existing;
            if (!create) throw new InvalidOperationException("No page is open. Use action \"open\" with a url first.");
            IPage page = await (await Launch()).NewPageAsync();
            page.DefaultTimeout = 30_000;
            page.DefaultNavigationTimeout = 30_000;
            pages[conversation] = page;
            return page;
        }
        finally { gate.Release(); }
    }

    /// <summary>Prints HTML to an A4 PDF in a throwaway page, so it never disturbs the page a conversation has open.</summary>
    internal static async Task<byte[]> PdfFromHtml(string html)
    {
        IPage page;
        await gate.WaitAsync();
        try { page = await (await Launch()).NewPageAsync(); } finally { gate.Release(); }
        try
        {
            await page.SetContentAsync(html);
            return await page.PdfDataAsync(new PdfOptions { Format = PuppeteerSharp.Media.PaperFormat.A4, PrintBackground = true,
                MarginOptions = new PuppeteerSharp.Media.MarginOptions { Top = "18mm", Bottom = "18mm", Left = "18mm", Right = "18mm" } });
        }
        finally { try { await page.CloseAsync(); } catch { } }
    }

    internal static async Task Close(string conversation)
    {
        if (pages.TryRemove(conversation, out IPage? p)) try { await p.CloseAsync(); } catch { }
    }
}

/// <summary>browser: look at a web page the way a person does. It can open a page, read its text, take a screenshot she can see, click, type,
/// press keys, scroll and run a little JavaScript, and it handles pages that need JavaScript to show anything.</summary>
internal sealed class BrowserTool : Tool
{
    private readonly Thread thread;
    internal BrowserTool(Thread thread) => this.thread = thread;

    internal override string Name => "browser";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "browser",
            description = "Use a real web browser: open a page, SEE it with a screenshot, read its text, click things, type into forms, scroll. " +
                          "Use it to look at how a website actually appears, to use pages that need JavaScript, or to check a site you built. " +
                          "Actions: open (url), screenshot, text, click (selector or the visible text of the thing), type (selector, text), press (key), scroll (direction), " +
                          "eval (a JavaScript expression), back, links, close. A screenshot comes back as an image you can look at. For plain reading, fetch_page is quicker.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    action    = new { type = "string", description = "open, screenshot, text, click, type, press, scroll, eval, back, links or close.", @enum = new[] { "open", "screenshot", "text", "click", "type", "press", "scroll", "eval", "back", "links", "close" } },
                    url       = new { type = "string", description = "For open: the address. Use http://localhost:PORT for something running on this machine, or file:///full/path/page.html for an HTML file you made." },
                    selector  = new { type = "string", description = "For click and type: a CSS selector, or text:Some visible words to click an element by its text." },
                    text      = new { type = "string", description = "For type: what to type. For press: a key like Enter or Tab. For eval: the JavaScript expression." },
                    direction = new { type = "string", description = "For scroll: down, up, top or bottom." },
                    full_page = new { type = "string", description = "For screenshot: true to capture the whole page, not just what's on screen." },
                },
                required = new[] { "action" }
            }
        }
    };

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        if (!thread.IsAdminChat) return "Only the owner, chatting in the app, can use the browser.";
        JsonElement root = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement;
        string Arg(string k) => root.TryGetProperty(k, out JsonElement v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";
        string action = Arg("action").ToLowerInvariant();

        try
        {
            if (action == "close") { await BrowserHost.Close(thread.Key); return "Browser page closed."; }
            IPage page = await BrowserHost.PageFor(thread.Key, create: action == "open");
            switch (action)
            {
                case "open":
                {
                    string url = Arg("url");
                    if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u) || u.Scheme is not ("http" or "https" or "file")) return "[Error: give a full address starting with http://, https:// or file://.]";
                    if (u.Scheme == "file" && !File.Exists(u.LocalPath)) return $"[Error: there is no file at {u.LocalPath}.]";
                    IResponse? resp = await page.GoToAsync(url, WaitUntilNavigation.Load);
                    return $"Opened {page.Url} — \"{await page.GetTitleAsync()}\" (HTTP {(int?)resp?.Status}). Take a screenshot to see it, or use text to read it.";
                }
                case "screenshot":
                {
                    byte[] png = await page.ScreenshotDataAsync(new ScreenshotOptions { Type = ScreenshotType.Png, FullPage = Arg("full_page").Equals("true", StringComparison.OrdinalIgnoreCase) });
                    if (png.Length > 18 * 1024 * 1024) return "[Error: that screenshot is too large. Try without full_page.]";
                    return ToolResult.AsImage(png, "image/png", $"Screenshot of {page.Url} (\"{await page.GetTitleAsync()}\").");
                }
                case "text":
                {
                    string text = await page.EvaluateExpressionAsync<string>("document.body ? document.body.innerText : ''");
                    text = text.Trim();
                    return $"{page.Url} — \"{await page.GetTitleAsync()}\"\n\n" + (text.Length > 12_000 ? text[..12_000] + $"\n… [{text.Length - 12_000:N0} more characters]" : text.Length == 0 ? "(the page has no visible text)" : text);
                }
                case "links":
                {
                    string json = await page.EvaluateExpressionAsync<string>("JSON.stringify([...document.querySelectorAll('a[href]')].slice(0,60).map(a => (a.innerText||'').trim().slice(0,60) + ' → ' + a.href))");
                    return string.Join("\n", JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>()) is { Length: > 0 } s ? s : "(no links)";
                }
                case "click":
                {
                    string sel = Arg("selector");
                    if (sel.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
                    {
                        string words = sel[5..].Trim();
                        bool hit = await page.EvaluateFunctionAsync<bool>(@"(words) => {
                            const els = [...document.querySelectorAll('a,button,input[type=button],input[type=submit],[role=button],[onclick],label,li,span,div')];
                            const m = els.filter(e => (e.innerText || e.value || '').trim().toLowerCase().includes(words.toLowerCase()) && e.offsetParent !== null)
                                         .sort((a, b) => (a.innerText||'').length - (b.innerText||'').length)[0];
                            if (!m) return false; m.click(); return true; }", words);
                        if (!hit) return $"[Error: nothing on the page shows the text \"{words}\".]";
                    }
                    else await page.ClickAsync(sel);
                    await Task.Delay(600);
                    return $"Clicked. Now at {page.Url} — \"{await page.GetTitleAsync()}\".";
                }
                case "type":
                    await page.TypeAsync(Arg("selector"), Arg("text"));
                    return "Typed.";
                case "press":
                    await page.Keyboard.PressAsync(Arg("text"));
                    await Task.Delay(400);
                    return $"Pressed {Arg("text")}. Now at {page.Url}.";
                case "scroll":
                {
                    string d = Arg("direction").ToLowerInvariant();
                    await page.EvaluateExpressionAsync(d switch { "up" => "window.scrollBy(0, -window.innerHeight*0.8)", "top" => "window.scrollTo(0,0)", "bottom" => "window.scrollTo(0, document.body.scrollHeight)", _ => "window.scrollBy(0, window.innerHeight*0.8)" });
                    return $"Scrolled {(d.Length == 0 ? "down" : d)}.";
                }
                case "eval":
                {
                    JsonElement r = await page.EvaluateExpressionAsync<JsonElement>(Arg("text"));
                    string s = r.ValueKind == JsonValueKind.Undefined ? "undefined" : r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : r.GetRawText();
                    return s.Length > 8000 ? s[..8000] + "…" : s;
                }
                case "back":
                    await page.GoBackAsync();
                    return $"Went back to {page.Url}.";
                default:
                    return $"[Error: unknown action \"{action}\".]";
            }
        }
        catch (InvalidOperationException e) { return $"[Error: {e.Message}]"; }
        catch (Exception e)
        {
            Shared.Logger.LogWarning(e, "[Browser] {Action} failed", action);
            return $"[Error: the browser couldn't do that: {e.Message.Split('\n')[0]}]";
        }
    }
}

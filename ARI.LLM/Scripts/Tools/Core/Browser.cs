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
    private static readonly HttpClient Fetcher = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly ConcurrentDictionary<string, Session> sessions = new();

    private const int DEFAULT_WIDTH = 1280;
    private const int DEFAULT_HEIGHT = 900;
    private const int MIN_WIDTH = 320;
    private const int MAX_WIDTH = 1920;
    private const int MIN_HEIGHT = 240;
    private const int MAX_HEIGHT = 1400;

    /// <summary>One conversation's browser: its tabs, which one it is looking at, and whether it is waiting for the person to do something.</summary>
    internal sealed class Session
    {
        internal readonly List<IPage> Tabs = new();
        internal int Active;
        internal string? Prompt;                      // what the person is being asked to do (login, captcha, …); null when nothing is waiting
        internal TaskCompletionSource? Handover;
        internal readonly List<(string Url, string Name, IPage Page)> Downloads = new();
        internal string? LastDialog;                  // what a pop-up (alert, confirm) said; they are accepted so a page never hangs on one
        internal ViewPortOptions Viewport = new() { Width = DEFAULT_WIDTH, Height = DEFAULT_HEIGHT };   // the size of the pane the person watches it in
        internal IPage? Page => Active >= 0 && Active < Tabs.Count ? Tabs[Active] : null;
    }

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
            // One profile for all her pages, so a site the person logs her into stays logged in across conversations and restarts.
            UserDataDir = Path.Combine(Paths.AppData, "Browser", "profile"),
            Args = new[] { "--no-sandbox", "--disable-dev-shm-usage", "--hide-scrollbars", "--window-size=1280,900" },
            DefaultViewport = new ViewPortOptions { Width = 1280, Height = 900 },
        });
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { browser?.CloseAsync().Wait(2000); } catch { } };
        return browser;
    }

    /// <summary>Downloads the page started since the last call, saved into the folder with the page's own cookies (so a logged-in download works). Returns the file names.</summary>
    internal static async Task<List<string>> CollectDownloads(string conversation, string folder)
    {
        List<string> saved = new();
        if (SessionOf(conversation) is not { } s) return saved;
        for (int wait = 0; wait < 6; wait++)
        {
            lock (s.Downloads) if (s.Downloads.Count > 0) break;
            await Task.Delay(400);
        }
        (string Url, string Name, IPage Page)[] items;
        lock (s.Downloads) { items = s.Downloads.ToArray(); s.Downloads.Clear(); }
        foreach (var d in items)
        {
            try
            {
                using HttpRequestMessage req = new(HttpMethod.Get, d.Url);
                var cookies = await d.Page.GetCookiesAsync(d.Url);
                if (cookies.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")));
                using HttpResponseMessage res = await Fetcher.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!res.IsSuccessStatusCode) continue;
                string name = string.IsNullOrWhiteSpace(d.Name) ? Path.GetFileName(new Uri(d.Url).LocalPath) : d.Name;
                name = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? "download" : name);
                string target = Path.Combine(folder, name);
                for (int n = 2; File.Exists(target); n++) target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
                await using (FileStream fs = File.Create(target)) await res.Content.CopyToAsync(fs);
                saved.Add(Path.GetFileName(target));
            }
            catch (Exception e) { Shared.Logger.LogWarning(e, "[Browser] Couldn't fetch the download {Url}", d.Url); }
        }
        return saved;
    }

    internal static async Task<IPage> PageFor(string conversation, bool create)
    {
        await gate.WaitAsync();
        try
        {
            Session session = sessions.GetOrAdd(conversation, _ => new Session());
            session.Tabs.RemoveAll(t => t.IsClosed);
            if (session.Active >= session.Tabs.Count) session.Active = Math.Max(0, session.Tabs.Count - 1);
            if (session.Page is { } existing) return existing;
            if (!create) throw new InvalidOperationException("No page is open. Use action \"open\" with a url first.");
            return await NewTab(session);
        }
        finally { gate.Release(); }
    }

    private static async Task<IPage> NewTab(Session session)
    {
        IPage page = await (await Launch()).NewPageAsync();
        page.DefaultTimeout = 30_000;
        page.DefaultNavigationTimeout = 30_000;
        // Chrome's own downloader stalls in a headless profile, so a download is spotted when it begins and fetched with the page's cookies instead.
        try { await page.Client.SendAsync("Page.setDownloadBehavior", new { behavior = "deny" }); } catch { }
        page.Client.MessageReceived += (_, e) =>
        {
            if (e.MessageID != "Page.downloadWillBegin") return;
            string url = e.MessageData.GetProperty("url").GetString() ?? "", name = e.MessageData.TryGetProperty("suggestedFilename", out var n) ? n.GetString() ?? "" : "";
            lock (session.Downloads) session.Downloads.Add((url, name, page));
        };
        page.Dialog += async (_, e) => { session.LastDialog = $"{e.Dialog.DialogType}: {e.Dialog.Message}"; try { await e.Dialog.Accept(); } catch { } };
        await page.SetViewportAsync(session.Viewport);
        session.Tabs.Add(page); session.Active = session.Tabs.Count - 1;
        return page;
    }

    internal static Session? SessionOf(string conversation) => sessions.TryGetValue(conversation, out Session? s) && s.Tabs.Any(t => !t.IsClosed) ? s : null;

    internal static async Task<IPage> OpenTab(string conversation) { await gate.WaitAsync(); try { return await NewTab(sessions.GetOrAdd(conversation, _ => new Session())); } finally { gate.Release(); } }

    internal static async Task CloseTab(string conversation)
    {
        if (SessionOf(conversation) is not { } s || s.Page is not { } p) return;
        try { await p.CloseAsync(); } catch { }
        s.Tabs.RemoveAll(t => t.IsClosed); s.Active = Math.Max(0, Math.Min(s.Active, s.Tabs.Count - 1));
    }

    /// <summary>Makes the pages the size of the pane the person watches them in, so the picture fills it exactly.</summary>
    internal static async Task Resize(string conversation, int width, int height)
    {
        if (SessionOf(conversation) is not { } session) return;
        session.Viewport = new ViewPortOptions { Width = Math.Clamp(width, MIN_WIDTH, MAX_WIDTH), Height = Math.Clamp(height, MIN_HEIGHT, MAX_HEIGHT) };
        foreach (IPage tab in session.Tabs.Where(t => !t.IsClosed)) await tab.SetViewportAsync(session.Viewport);
    }

    /// <summary>A picture of the page the conversation is looking at, for the person to watch. Null if there isn't one.</summary>
    internal static async Task<(byte[] Jpeg, string Url, string Title)?> Snapshot(string conversation)
    {
        if (SessionOf(conversation)?.Page is not { } page) return null;
        byte[] jpeg = await page.ScreenshotDataAsync(new ScreenshotOptions { Type = ScreenshotType.Jpeg, Quality = 62 });
        return (jpeg, page.Url, await page.GetTitleAsync());
    }

    /// <summary>The person using the page themselves, through the picture they see: a click, some typing, a key or a scroll.</summary>
    internal static async Task Input(string conversation, string kind, double x, double y, string text, double deltaY)
    {
        if (SessionOf(conversation)?.Page is not { } page) throw new InvalidOperationException("No page is open.");
        switch (kind)
        {
            case "click": await page.Mouse.ClickAsync((decimal)x, (decimal)y); break;
            case "type": await page.Keyboard.TypeAsync(text); break;
            case "key": await page.Keyboard.PressAsync(text); break;
            case "scroll": await page.Mouse.MoveAsync((decimal)x, (decimal)y); await page.Mouse.WheelAsync(0, (decimal)deltaY); break;
            case "navigate": if (Uri.TryCreate(text, UriKind.Absolute, out Uri? u) && u.Scheme is "http" or "https") await page.GoToAsync(text, WaitUntilNavigation.Load); break;
            default: throw new ArgumentException($"unknown input {kind}");
        }
    }

    /// <summary>Hands the page to the person (to log in, solve a check, pick something) and waits until they say they're done.</summary>
    internal static async Task<bool> WaitForPerson(string conversation, string prompt, CancellationToken ct)
    {
        if (SessionOf(conversation) is not { } s) throw new InvalidOperationException("No page is open. Use action \"open\" with a url first.");
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Prompt = prompt; s.Handover = tcs;
        try { await tcs.Task.WaitAsync(TimeSpan.FromMinutes(15), ct); return true; }
        catch (TimeoutException) { return false; }
        finally { s.Prompt = null; s.Handover = null; }
    }

    internal static bool FinishHandover(string conversation)
    {
        if (!sessions.TryGetValue(conversation, out Session? s) || s.Handover is not { } h) return false;
        h.TrySetResult(); return true;
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

    /// <summary>Loads a page of HTML in a throwaway tab and returns the JavaScript errors it raised, so a visual Ari wrote can be fixed before anyone sees it broken.</summary>
    internal static async Task<List<string>> CheckHtml(string html)
    {
        List<string> errors = new();
        IPage page;
        await gate.WaitAsync();
        try { page = await (await Launch()).NewPageAsync(); } finally { gate.Release(); }
        try
        {
            page.PageError += (_, e) => errors.Add(e.Message.Split('\n')[0]);
            page.Console += (_, e) => { if (e.Message.Type == ConsoleType.Error && !e.Message.Text.Contains("Failed to load resource")) errors.Add(e.Message.Text.Split('\n')[0]); };
            page.RequestFailed += (_, e) => errors.Add($"couldn't load {e.Request.Url}");
            await page.SetContentAsync(html, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Load }, Timeout = 15_000 });
            await Task.Delay(1500);
            if (await page.EvaluateExpressionAsync<int>("document.body ? document.body.innerText.trim().length + document.querySelectorAll('canvas,svg,img,table,input,button').length : 0") == 0)
                errors.Add("the page shows nothing");
        }
        catch (Exception e) { errors.Add(e.Message.Split('\n')[0]); }
        finally { try { await page.CloseAsync(); } catch { } }
        return errors.Distinct().Take(6).ToList();
    }

    internal static async Task Close(string conversation)
    {
        if (!sessions.TryRemove(conversation, out Session? s)) return;
        s.Handover?.TrySetResult();
        foreach (IPage p in s.Tabs) try { await p.CloseAsync(); } catch { }
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
                          "eval (a JavaScript expression), back, links, elements, select, upload (a file from your working folder), wait, tabs, new_tab, switch (tab number), close. elements lists everything clickable or typeable on the page as numbered refs; then click or type with selector ref:3.  A screenshot comes back as an image you can look at. For plain reading, fetch_page is quicker. " +
                          "The person can watch the page live in their chat window and use it themselves. When a page needs them (logging in, a code, a human check), open it, then call this same browser tool with action \"handover\" and text saying what to do (it is an action of browser, not a separate tool). It waits until they press Done, and you carry on logged in. Never ask for a password in chat, and never type a name, password or code for the person yourself: hand over instead.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    action    = new { type = "string", description = "open, screenshot, text, click, type, press, scroll, eval, back, links, elements, select, upload (a file from your working folder), wait, tabs, new_tab, switch, handover or close.", @enum = new[] { "open", "screenshot", "text", "click", "type", "press", "scroll", "eval", "back", "links", "elements", "select", "upload", "wait", "tabs", "new_tab", "switch", "handover", "close" } },
                    url       = new { type = "string", description = "For open: the address. Use http://localhost:PORT for something running on this machine, or file:///full/path/page.html for an HTML file you made." },
                    selector  = new { type = "string", description = "For click and type: a CSS selector, or text:Some visible words to click an element by its text, or ref:N for item N from the elements action." },
                    text      = new { type = "string", description = "For type: what to type. For press: a key like Enter or Tab. For eval: the JavaScript expression. For handover: what the person should do. For upload: the file name in your working folder (selector = the file input). For wait: a CSS selector to wait for, or milliseconds. For switch: the tab number." },
                    direction = new { type = "string", description = "For scroll: down, up, top or bottom." },
                    full_page = new { type = "string", description = "For screenshot: true to capture the whole page, not just what's on screen." },
                },
                required = new[] { "action" }
            }
        }
    };

    // One line for the chat, after each step: what she just did. The chat turns it into a card that opens the browser pane.
    internal override Func<string, string>? DisplayAfter => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args);
            JsonElement r = doc.RootElement;
            string A(string k) => r.TryGetProperty(k, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string action = A("action").ToLowerInvariant();
            string target = A("text").Length > 0 ? A("text") : A("selector");
            if (target.StartsWith("ref:", StringComparison.OrdinalIgnoreCase)) target = "an item";
            if (target.Length > 40) target = target[..40] + "…";
            string label = action switch
            {
                "close" => "",
                "open" or "new_tab" => Uri.TryCreate(A("url"), UriKind.Absolute, out Uri? u) ? (u.IsFile ? $"opened {Path.GetFileName(u.LocalPath)}" : $"opened {u.Host}{(u.AbsolutePath.Length > 1 ? u.AbsolutePath : "")}") : "opened a page",
                "screenshot" => "looked at the page",
                "text" or "links" or "elements" => "read the page",
                "click" => target.Length > 0 ? $"clicked {target}" : "clicked",
                "type" or "select" or "upload" => "filled in a form",
                "press" => $"pressed {A("key")}".TrimEnd(),
                "scroll" => "scrolled",
                "eval" => "ran a script on the page",
                "wait" => "waited for the page",
                "back" => "went back",
                "tabs" or "switch" => "switched tabs",
                "handover" or "ask_user" => "asked you to take over",
                _ => action,
            };
            return label.Length == 0 ? "" : $"\n<!--ari-browser: {ToolCallParser.EscapeLabel(label)}-->";
        }
        catch { return ""; }
    };

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        if (!thread.IsAdminChat) return "Only the owner, chatting in the app, can use the browser.";
        JsonElement root = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement;
        string Arg(string k) => root.TryGetProperty(k, out JsonElement v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";
        string action = Arg("action").ToLowerInvariant();
        string Sel() => Arg("selector") is var sl && sl.StartsWith("ref:", StringComparison.OrdinalIgnoreCase) ? $"[data-ari-ref=\"{sl[4..].Trim()}\"]" : Arg("selector");

        try
        {
            if (action == "close") { await BrowserHost.Close(thread.Key); return "Browser closed."; }
            if (action == "new_tab")
            {
                IPage tab = await BrowserHost.OpenTab(thread.Key);
                if (Arg("url") is { Length: > 0 } nu) { if (!Uri.TryCreate(nu, UriKind.Absolute, out Uri? tu) || tu.Scheme is not ("http" or "https" or "file")) return "[Error: give a full address starting with http://, https:// or file://.]"; await tab.GoToAsync(nu, WaitUntilNavigation.Load); }
                return $"Opened a new tab ({BrowserHost.SessionOf(thread.Key)!.Tabs.Count} open).";
            }
            if (action is "tabs" or "switch")
            {
                BrowserHost.Session? ss = BrowserHost.SessionOf(thread.Key);
                if (ss is null) return "No page is open.";
                if (action == "switch")
                {
                    if (!int.TryParse(Arg("text"), out int n) || n < 1 || n > ss.Tabs.Count) return $"[Error: give a tab number from 1 to {ss.Tabs.Count}.]";
                    ss.Active = n - 1;
                }
                return string.Join("\n", ss.Tabs.Select((t, i) => $"{(i == ss.Active ? "*" : " ")} {i + 1}. {t.Url}"));
            }
            IPage page = await BrowserHost.PageFor(thread.Key, create: action == "open");
            string folder = thread.FilesystemRoot is { Length: > 0 } fr ? fr : Paths.ScratchpadDir(thread.Key);
            async Task<string> After(string said)
            {
                if (action is "click" or "press")
                {
                    List<string> got = await BrowserHost.CollectDownloads(thread.Key, Directory.CreateDirectory(folder).FullName);
                    if (got.Count > 0) said += $" Downloaded into your working folder: {string.Join(", ", got)}.";
                }
                if (BrowserHost.SessionOf(thread.Key) is { LastDialog: { } d } ss) { said += $" A pop-up appeared and was accepted ({d})."; ss.LastDialog = null; }
                return said;
            }
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
                    string sel = Sel();
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
                    return await After($"Clicked. Now at {page.Url} — \"{await page.GetTitleAsync()}\".");
                }
                case "type":
                    await page.TypeAsync(Sel(), Arg("text"));
                    return "Typed.";
                case "upload":
                {
                    string file = Path.GetFullPath(Path.IsPathRooted(Arg("text")) ? Arg("text") : Path.Combine(folder, Arg("text")));
                    if (!file.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar) || !File.Exists(file)) return $"[Error: there is no file {Arg("text")} in your working folder.]";
                    IElementHandle? input = await page.QuerySelectorAsync(Sel());
                    if (input is null) return "[Error: no file input matches that selector. Use elements to find it.]";
                    await input.UploadFileAsync(file);
                    return $"Attached {Path.GetFileName(file)}.";
                }
                case "select":
                {
                    string[] chosen = await page.SelectAsync(Sel(), Arg("text"));
                    return chosen.Length > 0 ? $"Selected {Arg("text")}." : $"[Error: no option with the value \"{Arg("text")}\" there. Use eval to list the options.]";
                }
                case "elements":
                {
                    string json = await page.EvaluateExpressionAsync<string>(@"(() => {
                        document.querySelectorAll('[data-ari-ref]').forEach(e => e.removeAttribute('data-ari-ref'));
                        const sel = 'a[href],button,input,select,textarea,summary,[role=button],[role=link],[role=tab],[role=menuitem],[role=checkbox],[onclick],[contenteditable=true]';
                        const out = []; let n = 0;
                        for (const e of document.querySelectorAll(sel)) {
                            const r = e.getBoundingClientRect(); const st = getComputedStyle(e);
                            if (r.width < 2 || r.height < 2 || st.visibility === 'hidden' || st.display === 'none' || e.disabled) continue;
                            n++; e.setAttribute('data-ari-ref', n);
                            const tag = e.tagName.toLowerCase(), type = e.getAttribute('type') || '';
                            const label = (e.innerText || e.value || e.getAttribute('aria-label') || e.placeholder || e.title || e.name || e.alt || '').trim().replace(/\s+/g, ' ').slice(0, 70);
                            const kind = tag === 'a' ? 'link' : tag === 'input' ? (type || 'text') + ' field' : tag === 'textarea' ? 'text area' : tag === 'select' ? 'dropdown' : tag;
                            out.push(n + ' [' + kind + '] ' + label + (tag === 'a' && e.href ? ' → ' + e.href.slice(0, 80) : '') + (e.checked ? ' (checked)' : '') + (tag === 'input' && type !== 'password' && e.value && type !== 'submit' && type !== 'button' ? ' = ' + e.value.slice(0, 40) : ''));
                            if (n >= 80) break;
                        }
                        return JSON.stringify(out); })()");
                    string[] items = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
                    return items.Length == 0 ? "(nothing clickable or typeable on this page)" : $"{page.Url}\n" + string.Join("\n", items) + "\nUse selector ref:N with click, type or select.";
                }
                case "press":
                    await page.Keyboard.PressAsync(Arg("text"));
                    await Task.Delay(400);
                    return await After($"Pressed {Arg("text")}. Now at {page.Url}.");
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
                case "wait":
                {
                    string w = Arg("text");
                    if (int.TryParse(w, out int ms)) await Task.Delay(Math.Clamp(ms, 0, 30_000));
                    else await page.WaitForSelectorAsync(w, new WaitForSelectorOptions { Timeout = 15_000 });
                    return $"Waited. Now at {page.Url} — \"{await page.GetTitleAsync()}\".";
                }
                case "handover": case "ask_user":
                {
                    string ask = Arg("text").Trim();
                    if (ask.Length == 0) return "[Error: say what the person should do in text.]";
                    bool done = await BrowserHost.WaitForPerson(thread.Key, ask, thread.Ct);
                    return done ? $"The person has finished. The page is now at {page.Url} — \"{await page.GetTitleAsync()}\". Take a screenshot or read the text to continue."
                                : "[The person didn't respond in 15 minutes. Tell them you're ready when they are, and stop.]";
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

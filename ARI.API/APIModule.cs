using ARI.API.Auth;
using ARI.API.Controllers;
using ARI.API.Data;
using ARI.Common;
using ARI.Listener;
using ARI.LLM;
using ARI.VoiceSynthesis;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace ARI.API;

public class APIModule : IAsyncDisposable
{
    private readonly ILoggerFactory       loggerFactory;
    private readonly APIConfig            config;
    private readonly VoiceSynthesisConfig voiceSynthesisConfig;
    private readonly PersistentData       persistentData;
    private readonly SystemInfo           systemInfo;
    private WebApplication? app;

    public APIModule(
        ILoggerFactory       loggerFactory,
        APIConfig            config,
        VoiceSynthesisConfig voiceSynthesisConfig,
        string               modelsPath,
        PersistentData       persistentData)
    {
        this.loggerFactory        = loggerFactory;
        this.config               = config;
        this.voiceSynthesisConfig = voiceSynthesisConfig;
        this.persistentData       = persistentData;
        this.systemInfo           = new SystemInfo(modelsPath);
    }

    public async Task Start(CancellationToken cancellationToken)
    {
        VoiceController.ClearStaging();   // voice uploads/processing output are non-persistent

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Paths.BuildPath,
            WebRootPath     = Paths.WwwRoot,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(loggerFactory));

        LLMModule.ConventionsProvider = ConventionsStore.Get;
        builder.WebHost.UseUrls($"http://0.0.0.0:{config.Port}");
        builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(2));
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 512L * 1024 * 1024;
        });

        string keysDir = Paths.Keys;
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new System.IO.DirectoryInfo(keysDir))
            .SetApplicationName("ARI");

        // Web Push (PWA notifications). Owns the VAPID keypair + subscription store; Ari's proactive
        // path rings the phone via Modules.WebPush. Registered statically so controllers reach it like Llm.
        string pushDir = Paths.Push;
        // VAPID subject is just a contact field on the push keypair — a generic mailto is fine.
        WebPushModule webPush = new(loggerFactory.CreateLogger<WebPushModule>(), pushDir, "mailto:owner@localhost");
        Modules.Register(webPush: webPush);

        builder.Services.AddSingleton<UserStore>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(voiceSynthesisConfig);
        builder.Services.AddSingleton(persistentData);
        builder.Services.AddSingleton(systemInfo);
        builder.Services.AddSingleton<ProjectStore>();
        builder.Services.AddSingleton<ProjectServiceAdapter>();

        // Clear stale staging folders from a previous run
        string stagingRoot = Path.Combine(Path.GetTempPath(), "ari-voice-staging");
        if (Directory.Exists(stagingRoot))
            try { Directory.Delete(stagingRoot, recursive: true); } catch { /* best-effort */ }

        builder.Services.AddControllers(options => { options.Filters.Add<ResponseShapeFilter>(); options.Filters.Add<SafeNames.Filter>(); })
            .AddApplicationPart(typeof(ThreadsController).Assembly);

        // ARI gates every route (REST and WebSocket) behind its own JWT+session auth — see
        // AuthMiddleware and the explicit checks around the WebSocket upgrades below. A reverse proxy
        // in front is still good defense-in-depth for WAN exposure, but is no longer load-bearing.

        app = builder.Build();

        // ARI.LLM's project_tools reach project creation/binding only through this interface — never
        // a direct reference to ProjectStore/ProjectServiceAdapter (the wrong dependency direction).
        Modules.Register(projects: app.Services.GetRequiredService<ProjectServiceAdapter>());

        // Ensure every project has a server-managed folder — migrates legacy RemoteFs projects.
        app.Services.GetRequiredService<ProjectStore>().MigrateToServerFs();

        app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
        {
            Exception? ex  = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            ILogger log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ARI.API");
            log.LogError(ex, "Unhandled exception on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);

            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                // A value of the wrong type in a JSON body is the caller's mistake (400); anything else is ours (500).
                bool badInput = ex is FormatException or System.Text.Json.JsonException
                    || (ex is InvalidOperationException && ex.Source == "System.Text.Json") || ex is ArgumentException { ParamName: not null } && ex.Source == "System.Private.CoreLib";
                ctx.Response.StatusCode = badInput ? 400 : 500;
                await ctx.Response.WriteAsJsonAsync(new { error = badInput ? "A value in the request was not the right kind." : "Something went wrong." });
                return;
            }

            ctx.Response.StatusCode  = 500;
            ctx.Response.ContentType = "text/html";
            await ctx.Response.WriteAsync(
                "<!DOCTYPE html><html><head><meta charset='utf-8'/>" +
                "<meta http-equiv='refresh' content='3'/>" +
                "<style>body{font-family:sans-serif;background:#f3f4f6;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}" +
                "div{text-align:center;color:#6b7280}h2{color:#374151;margin-bottom:8px}</style></head><body>" +
                "<div><h2>Something went wrong</h2><p>Reloading in 3 seconds…</p></div>" +
                "</body></html>");
        }));

        // Asset filenames are content-hashed, so they can be cached forever; the documents that POINT at
        // them must never be, or a rebuild is invisible until the browser is manually cleared.
        Action<StaticFileResponseContext> cacheHeaders = ctx =>
        {
            string file = ctx.File.Name;
            // The logo images, colours and theme script are changed from the control panel (BrandingStore, ThemeStore),
            // so they revalidate instead of being cached for a year.
            if (file is "index.html" or "sw.js" or "controlpanel.html" or "logo-black.png" or "logo-white.png" or "BlackCode.png"
                or "theme.js" or "colours.css" or "colours.json")
            {
                ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                ctx.Context.Response.Headers["Pragma"]        = "no-cache";
                ctx.Context.Response.Headers["Expires"]       = "0";
            }
            else
            {
                ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
            }
        };

        // Uploaded logos replace the built-in image URLs (see BrandingStore); must run before the static files.
        app.Use(BrandingStore.Middleware);

        // Rewrite bare "/" to "/index.html" before the auth middleware runs, so the browser can
        // fetch the SPA shell without a token (the JS inside handles auth state itself).
        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Paths.WwwRoot),
            RequestPath  = "",
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider      = new PhysicalFileProvider(Paths.WwwRoot),
            RequestPath       = "",
            OnPrepareResponse = cacheHeaders,
        });

        // Desktop client WebSocket — MUST be before UseRouting. Aggressive keepalive: the desktop client's
        // socket was observed dropping mid-turn during long silent stretches (model decoding, no traffic),
        // losing in-flight tool calls; protocol-level pings keep intermediaries from idling it out.
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path == "/api/v1/client" || ctx.Request.Path == "/api/v1/listener/stream")
            {
                // A page on some other site (or another port on this machine) must not be able to open these sockets with
                // the visitor's cookie: when the browser says where the page came from, it has to be this server.
                if (ctx.Request.Headers.Origin.ToString() is { Length: > 0 } origin
                    && (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri) || !originUri.Authority.Equals(ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Cross-site connections are not allowed." });
                    return;
                }
                // These sockets are accepted before UseMiddleware<AuthMiddleware>() runs (WebSocket
                // upgrades can't carry the normal pipeline's response), so authenticate here explicitly —
                // same JWT+session check as every REST endpoint. Unauthenticated requests never reach
                // AcceptWebSocketAsync.
                UserStore   store = ctx.RequestServices.GetRequiredService<UserStore>();
                AuthService auth  = ctx.RequestServices.GetRequiredService<AuthService>();
                System.Security.Claims.ClaimsPrincipal? principal = AuthMiddleware.Authenticate(ctx, store, auth);
                if (principal is null)
                {
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Sign in first." });
                    return;
                }
                ctx.User = principal;
            }

            if (ctx.Request.Path == "/api/v1/client")
            {
                if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

                LLMModule? llmSvc = (LLMModule?)Modules.Llm;
                if (llmSvc is null) { ctx.Response.StatusCode = 503; return; }

                // Binding tools to a thread is using that thread: only its owner (or an admin) may.
                if (ctx.Request.Query["threadKey"].ToString() is { Length: > 0 } wanted && !ThreadAccess.CanUse(ctx.User, llmSvc, wanted))
                {
                    ctx.Response.StatusCode = 403;
                    return;
                }

                WebSocket ws  = await ctx.WebSockets.AcceptWebSocketAsync();
                ILogger log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ARI.Client");
                await ClientWebSocket.HandleAsync(ws, ctx, llmSvc, log);
                return;
            }

            // Audio ingress for the Speech pipeline — browser mic / client streams PCM here.
            if (ctx.Request.Path == "/api/v1/listener/stream")
            {
                if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

                // The spoken replies are written into a thread, so the caller must be allowed to use it: its owner,
                // or an admin. And a name sent by the client is never trusted: the speaker is whoever signed in.
                string threadKey = ctx.Request.Query["threadKey"].ToString();
                LLMModule? llmForAudio = (LLMModule?)Modules.Llm;
                if (string.IsNullOrWhiteSpace(threadKey)) { ctx.Response.StatusCode = 400; return; }
                if (llmForAudio is not null && !ThreadAccess.CanUse(ctx.User, llmForAudio, threadKey)) { ctx.Response.StatusCode = 403; return; }
                if (Modules.Listener is not ARI.Listener.ListenerModule listener) { ctx.Response.StatusCode = 503; return; }

                string source    = ctx.Request.Query["source"].ToString() is { Length: > 0 } s ? s : "web";
                string? userId   = ctx.User.FindFirst("displayName")?.Value ?? ctx.User.Identity?.Name;
                ListenerSessionContext context      = new ListenerSessionContext(source, threadKey, userId);

                WebSocket ws = await ctx.WebSockets.AcceptWebSocketAsync();
                await listener.HandleConnectionAsync(ws, context, ctx.RequestAborted);
                return;
            }
            await next();
        });

        app.UseMiddleware<AuthMiddleware>();
        app.UseRouting();

        app.MapControllers();
        // The fallback runs its OWN static-file middleware and ignores the options set above, so browsing
        // to "/" served index.html with default caching. That is why a rebuilt UI kept loading the old
        // bundle: the page was fresh on disk and a month old in the browser.
        // An address under /api/ that no route owns is a real "not found", never the web page.
        app.MapFallback("/api/{**rest}", async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = "Not found." });
        });
        app.MapFallbackToFile("{*path:regex(^(?!api/).*$)}", "index.html", new StaticFileOptions
        {
            FileProvider      = new PhysicalFileProvider(Paths.WwwRoot),
            OnPrepareResponse = cacheHeaders,
        });

        // Build the user store now (not on the first request) so a fresh install's Setup Token is in the log at boot.
        app.Services.GetRequiredService<UserStore>();

        await app.StartAsync(cancellationToken);

        loggerFactory.CreateLogger("ARI.API").LogInformation("ARI.API is ready. Listening on http://0.0.0.0:{Port}", config.Port);
    }

    public async Task Stop(CancellationToken cancellationToken)
    {
        if (app is not null)
            await app.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
            await app.DisposeAsync();
    }
}

internal class ForwardingLoggerProvider(ILoggerFactory inner) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => inner.CreateLogger(categoryName);
    public void Dispose() { }
}

using ARI.API.Controllers;
using ARI.API.Data;
using ARI.Common;
using ARI.LLM;
using ARI.VoiceSynthesis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
        // VAPID subject is just a contact field on the push keypair — a generic mailto is fine;
        // auth (who may reach ARI) is handled by whatever reverse proxy sits in front, not here.
        WebPushModule webPush = new(loggerFactory.CreateLogger<WebPushModule>(), pushDir, "mailto:owner@localhost");
        Modules.Register(webPush: webPush);

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(voiceSynthesisConfig);
        builder.Services.AddSingleton(persistentData);
        builder.Services.AddSingleton(systemInfo);
        builder.Services.AddSingleton<ProjectStore>();

        // Clear stale staging folders from a previous run
        string stagingRoot = Path.Combine(Path.GetTempPath(), "ari-voice-staging");
        if (Directory.Exists(stagingRoot))
            try { Directory.Delete(stagingRoot, recursive: true); } catch { /* best-effort */ }

        builder.Services.AddControllers()
            .AddApplicationPart(typeof(ThreadsController).Assembly);

        // ARI has no built-in auth. It binds to localhost/LAN and expects any public exposure to be
        // gated by a reverse proxy in front of it (e.g. Cloudflare Access, Authentik, an nginx
        // basic-auth layer). Keeping auth out of ARI keeps it identity-provider-agnostic.

        app = builder.Build();

        app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
        {
            var ex  = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ARI.API");
            log.LogError(ex, "Unhandled exception on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);

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

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Paths.WwwRoot),
            RequestPath  = "",
            OnPrepareResponse = ctx =>
            {
                string file = ctx.File.Name;
                if (file == "index.html" || file == "sw.js")
                {
                    ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                    ctx.Context.Response.Headers["Pragma"]        = "no-cache";
                    ctx.Context.Response.Headers["Expires"]       = "0";
                }
                else
                {
                    ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                }
            },
        });

        // Desktop client WebSocket — MUST be before UseRouting. Aggressive keepalive: the desktop client's
        // socket was observed dropping mid-turn during long silent stretches (model decoding, no traffic),
        // losing in-flight tool calls; protocol-level pings keep intermediaries from idling it out.
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path == "/api/client")
            {
                if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

                LLMModule? llmSvc = (LLMModule?)Modules.Llm;
                if (llmSvc is null) { ctx.Response.StatusCode = 503; return; }

                var ws  = await ctx.WebSockets.AcceptWebSocketAsync();
                var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ARI.Client");
                await ClientWebSocket.HandleAsync(ws, ctx, llmSvc, log);
                return;
            }

            // Audio ingress for the Speech pipeline — browser mic / client streams PCM here.
            if (ctx.Request.Path == "/api/listener/stream")
            {
                if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
                if (Modules.Listener is not ARI.Listener.ListenerModule listener) { ctx.Response.StatusCode = 503; return; }

                string source    = ctx.Request.Query["source"].ToString() is { Length: > 0 } s ? s : "web";
                string threadKey = ctx.Request.Query["threadKey"].ToString();
                string? userId   = ctx.Request.Query["userId"].ToString() is { Length: > 0 } u ? u : null;
                var context      = new ARI.Listener.ListenerSessionContext(source, threadKey, userId);

                var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                await listener.HandleConnectionAsync(ws, context, ctx.RequestAborted);
                return;
            }
            await next();
        });

        app.UseRouting();

        app.MapControllers();
        app.MapFallbackToFile("index.html");

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

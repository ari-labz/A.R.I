using ARI.API.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace ARI.API;

public class WebPanelConfig
{
    public int Port { get; init; } = 5000;
    public string GoogleClientId { get; init; } = "";
    public string GoogleClientSecret { get; init; } = "";
    public string AllowedEmail { get; init; } = "";
    public IReadOnlyList<string> AllowedEmails { get; init; } = Array.Empty<string>();

    /// <summary>Returns the effective allowlist — AllowedEmails if populated, otherwise the single AllowedEmail.</summary>
    internal IReadOnlyList<string> EffectiveAllowedEmails =>
        AllowedEmails.Count > 0 ? AllowedEmails : (AllowedEmail.Length > 0 ? new[] { AllowedEmail } : Array.Empty<string>());
    public string LogPath    { get; init; } = "";
    public string F5Path     { get; init; } = "";
    public string VoicesPath { get; init; } = "";
}

public class ApiService : IAsyncDisposable
{
    private readonly ILoggerFactory loggerFactory;
    private readonly LlmServiceHolder holder;
    private readonly WebPanelConfig config;
    private readonly SystemInfoHolder systemInfo;
    private readonly DiscordServiceHolder discordHolder;
    private readonly SpeechQueueHolder speechHolder;
    private WebApplication? app;

    // Cached internet connectivity — checked every 30 s in the background.
    // When offline, localhost connections bypass Google auth (OAuth can't work anyway).
    private static volatile bool _online = true;
    private static readonly HttpClient _pingClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly Timer _pingTimer = new(async _ =>
    {
        try   { await _pingClient.GetAsync("https://accounts.google.com/"); _online = true;  }
        catch { _online = false; }
    }, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));

    public LlmServiceHolder     Holder        => holder;
    public SystemInfoHolder     SystemInfo    => systemInfo;
    public DiscordServiceHolder DiscordHolder => discordHolder;
    public SpeechQueueHolder    SpeechHolder  => speechHolder;

    public ApiService(ILoggerFactory loggerFactory, WebPanelConfig config)
    {
        this.loggerFactory    = loggerFactory;
        this.holder           = new LlmServiceHolder();
        this.config           = config;
        this.systemInfo       = new SystemInfoHolder();
        this.discordHolder    = new DiscordServiceHolder();
        this.speechHolder     = new SpeechQueueHolder();
    }

    public async Task Start(CancellationToken cancellationToken)
    {
        string exeDir = AppContext.BaseDirectory;
        // Prefer ARI.UI/dist (dev/source layout), fall back to wwwroot (deployed layout)
        string uiDist    = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "ARI.UI", "dist"));
        string wwwrootDir = Directory.Exists(uiDist) ? uiDist : Path.Combine(exeDir, "wwwroot");
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = exeDir,
            WebRootPath = wwwrootDir,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(loggerFactory));

        builder.WebHost.UseUrls($"http://0.0.0.0:{config.Port}");
        builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(2));
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 512L * 1024 * 1024; // 512 MB for voice training uploads
        });

        builder.Services.AddSingleton(holder);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(systemInfo);
        builder.Services.AddSingleton(new VoiceTrainerHolder());
        builder.Services.AddSingleton(new DiscordServiceHolder());
        builder.Services.AddSingleton(speechHolder);

        // Clear any stale staging folders from a previous run
        string stagingRoot = Path.Combine(Path.GetTempPath(), "ari-voice-staging");
        if (Directory.Exists(stagingRoot))
        {
            try { Directory.Delete(stagingRoot, recursive: true); } catch { /* best-effort */ }
        }
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(ThreadsController).Assembly);

        builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.LoginPath = "/auth/login";
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
        })
        .AddGoogle(options =>
        {
            options.ClientId = config.GoogleClientId;
            options.ClientSecret = config.GoogleClientSecret;
            options.CallbackPath = "/auth/callback";
            options.Events.OnTicketReceived = ctx =>
            {
                string? email = ctx.Principal?.FindFirstValue(ClaimTypes.Email);
                if (!config.EffectiveAllowedEmails.Any(e => string.Equals(email, e, StringComparison.OrdinalIgnoreCase)))
                {
                    ctx.Fail($"Access denied.");
                    ctx.HandleResponse();
                    ctx.Response.Redirect("/auth/login?error=unauthorized");
                }
                return Task.CompletedTask;
            };
        });

        builder.Services.AddAuthorization();

        app = builder.Build();

        app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
        {
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
            FileProvider = new PhysicalFileProvider(wwwrootDir),
            RequestPath = "",
        });

        // ── Desktop client WebSocket — MUST be before UseRouting so it
        //    short-circuits before endpoint routing can intercept it ──────────────
        app.UseWebSockets();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path == "/api/client")
            {
                if (!ctx.WebSockets.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    return;
                }

                var holder = ctx.RequestServices.GetRequiredService<LlmServiceHolder>();
                if (holder.Service is null)
                {
                    ctx.Response.StatusCode = 503;
                    return;
                }

                var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ARI.Client");
                await ClientWebSocket.HandleAsync(ws, holder.Service, log);
                return; // don't call next — request is fully handled
            }

            await next();
        });

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        // Block all routes unless authenticated with the whitelisted email
        app.Use(async (ctx, next) =>
        {
            // Localhost + offline: allow through (Google OAuth can't work without internet)
            var remoteIp   = ctx.Connection.RemoteIpAddress;
            bool isLocalhost = remoteIp != null && (System.Net.IPAddress.IsLoopback(remoteIp) || remoteIp.ToString() == "::1");
            if (isLocalhost && !_online) { await next(); return; }

            bool isAuthPath = ctx.Request.Path == "/auth/login"
                || ctx.Request.Path == "/auth/callback"
                || ctx.Request.Path.StartsWithSegments("/signin-google")
                || ctx.Request.Path.StartsWithSegments("/images")
                || ctx.Request.Path.StartsWithSegments("/assets")
                || ctx.Request.Path == "/favicon.ico"
                || ctx.Request.Path == "/manifest.json"
                || ctx.Request.Path == "/sw.js";

            if (isAuthPath) { await next(); return; }

            if (ctx.User.Identity?.IsAuthenticated == true)
            {
                string? email = ctx.User.FindFirstValue(ClaimTypes.Email);
                if (!config.EffectiveAllowedEmails.Any(e => string.Equals(email, e, StringComparison.OrdinalIgnoreCase)))
                {
                    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    ctx.Response.Redirect("/auth/login?error=unauthorized");
                    return;
                }
            }
            else
            {
                ctx.Response.Redirect("/auth/login");
                return;
            }

            await next();
        });

        app.MapControllers();
        // SPA fallback — any non-API, non-static path serves index.html
        app.MapFallbackToFile("index.html");

        await app.StartAsync(cancellationToken);

        ILogger logger = loggerFactory.CreateLogger("ARI.API");
        logger.LogInformation("ARI.API is ready. Listening on http://0.0.0.0:{Port}", config.Port);
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

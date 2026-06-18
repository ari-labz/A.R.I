using ARI.API.Controllers;
using ARI.API.Data;
using ARI.LLM;
using ARI.Voice;
using ARI.VoiceSynthesis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace ARI.API;

public class APIModule : IAsyncDisposable
{
    private readonly ILoggerFactory        loggerFactory;
    private readonly APIConfig             config;
    private readonly VoiceSynthesisConfig  voiceSynthesisConfig;
    private readonly LLMModule?            llm;
    private readonly PersistentData        persistentData;
    private readonly VoiceModule?             voiceService;
    private readonly VoiceSynthesisModule     voiceTraining;
    private readonly SystemInfo            systemInfo;
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

    public APIModule(
        ILoggerFactory        loggerFactory,
        APIConfig             config,
        VoiceSynthesisConfig  voiceSynthesisConfig,
        string                modelsPath,
        LLMModule?            llm,
        PersistentData        persistentData,
        VoiceModule?          voiceService,
        VoiceSynthesisModule  voiceTraining)
    {
        this.loggerFactory        = loggerFactory;
        this.config               = config;
        this.voiceSynthesisConfig = voiceSynthesisConfig;
        this.llm                  = llm;
        this.persistentData       = persistentData;
        this.voiceService         = voiceService;
        this.voiceTraining        = voiceTraining;
        this.systemInfo           = new SystemInfo(llm, modelsPath);
    }

    public async Task Start(CancellationToken cancellationToken)
    {
        string exeDir     = AppContext.BaseDirectory;
        string uiDist     = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "ARI.UI", "dist"));
        string wwwrootDir = Directory.Exists(uiDist) ? uiDist : Path.Combine(exeDir, "wwwroot");

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = exeDir,
            WebRootPath     = wwwrootDir,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(loggerFactory));

        builder.WebHost.UseUrls($"http://0.0.0.0:{config.Port}");
        builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(2));
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 512L * 1024 * 1024;
        });

        string keysDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ari", "Server", "keys");
        Directory.CreateDirectory(keysDir);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new System.IO.DirectoryInfo(keysDir))
            .SetApplicationName("ARI");

        // Register services directly — no holders, no post-build .Set() calls
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(voiceSynthesisConfig);
        builder.Services.AddSingleton(persistentData);
        builder.Services.AddSingleton(voiceTraining);
        builder.Services.AddSingleton(systemInfo);
        builder.Services.AddSingleton<ProjectStore>();

        // Optional services — registered only when the module is enabled
        if (llm         is not null) builder.Services.AddSingleton(llm);
        if (voiceService is not null) builder.Services.AddSingleton(voiceService);

        // Clear stale staging folders from a previous run
        string stagingRoot = Path.Combine(Path.GetTempPath(), "ari-voice-staging");
        if (Directory.Exists(stagingRoot))
            try { Directory.Delete(stagingRoot, recursive: true); } catch { /* best-effort */ }

        builder.Services.AddControllers()
            .AddApplicationPart(typeof(ThreadsController).Assembly);

        bool useGoogleAuth = !string.IsNullOrEmpty(config.Google.ClientId);

        var authBuilder = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            if (useGoogleAuth)
                options.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.LoginPath       = "/auth/login";
            options.ExpireTimeSpan  = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            options.Events.OnSigningIn = ctx =>
            {
                ctx.Properties.IsPersistent = true;
                return Task.CompletedTask;
            };
        });

        if (useGoogleAuth)
        {
            authBuilder.AddGoogle(options =>
            {
                options.ClientId     = config.Google.ClientId;
                options.ClientSecret = config.Google.ClientSecret;
                options.CallbackPath = "/auth/callback";
                options.Events.OnTicketReceived = ctx =>
                {
                    string? email = ctx.Principal?.FindFirstValue(ClaimTypes.Email);
                    if (!config.Google.AllowedEmails.Any(e => string.Equals(email, e, StringComparison.OrdinalIgnoreCase)))
                    {
                        ctx.Fail("Access denied.");
                        ctx.HandleResponse();
                        ctx.Response.Redirect("/auth/login?error=unauthorized");
                    }
                    return Task.CompletedTask;
                };
            });
        }

        builder.Services.AddAuthorization();

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
            FileProvider = new PhysicalFileProvider(wwwrootDir),
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

        // Desktop client WebSocket — MUST be before UseRouting
        app.UseWebSockets();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path == "/api/client")
            {
                if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

                LLMModule? llmSvc = ctx.RequestServices.GetService<LLMModule>();
                if (llmSvc is null) { ctx.Response.StatusCode = 503; return; }

                var ws  = await ctx.WebSockets.AcceptWebSocketAsync();
                var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ARI.Client");
                await ClientWebSocket.HandleAsync(ws, ctx, llmSvc, log);
                return;
            }
            await next();
        });

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        app.Use(async (ctx, next) =>
        {
            if (string.IsNullOrEmpty(config.Google.ClientId)) { await next(); return; }

            var remoteIp     = ctx.Connection.RemoteIpAddress;
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
                if (!config.Google.AllowedEmails.Any(e => string.Equals(email, e, StringComparison.OrdinalIgnoreCase)))
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

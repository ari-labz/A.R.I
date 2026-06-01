using ARI.WebPanel.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace ARI.WebPanel;

public class WebPanelConfig
{
    public int Port { get; init; } = 5000;
    public string GoogleClientId { get; init; } = "";
    public string GoogleClientSecret { get; init; } = "";
    public string AllowedEmail { get; init; } = "";
}

public class WebPanelService : IAsyncDisposable
{
    private readonly ILoggerFactory loggerFactory;
    private readonly LlmServiceHolder holder;
    private readonly WebPanelConfig config;
    private WebApplication? app;

    public LlmServiceHolder Holder => holder;

    public WebPanelService(ILoggerFactory loggerFactory, WebPanelConfig config)
    {
        this.loggerFactory = loggerFactory;
        this.holder = new LlmServiceHolder();
        this.config = config;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(loggerFactory));

        builder.WebHost.UseUrls($"http://0.0.0.0:{config.Port}");

        builder.Services.AddSingleton(holder);
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(ChatController).Assembly);

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
                if (!string.Equals(email, config.AllowedEmail, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Fail($"Access denied: {email} is not an authorised user.");
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

        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        // Block all routes unless authenticated — redirect to login
        app.Use(async (ctx, next) =>
        {
            if (!ctx.User.Identity?.IsAuthenticated == true
                && ctx.Request.Path != "/auth/login"
                && ctx.Request.Path != "/auth/callback"
                && !ctx.Request.Path.StartsWithSegments("/signin-google"))
            {
                ctx.Response.Redirect("/auth/login");
                return;
            }
            await next();
        });

        app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Chat}/{action=Index}/{id?}");

        await app.StartAsync(cancellationToken);

        ILogger logger = loggerFactory.CreateLogger("ARI.WebPanel");
        logger.LogInformation("Web panel is ready. Listening on http://0.0.0.0:{Port}", config.Port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
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

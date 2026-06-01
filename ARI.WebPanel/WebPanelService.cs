using ARI.WebPanel.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ARI.WebPanel;

public class WebPanelService : IAsyncDisposable
{
    private readonly ILoggerFactory loggerFactory;
    private readonly LlmServiceHolder holder;
    private readonly int port;
    private WebApplication? app;

    public LlmServiceHolder Holder => holder;

    public WebPanelService(ILoggerFactory loggerFactory, int port)
    {
        this.loggerFactory = loggerFactory;
        this.holder = new LlmServiceHolder();
        this.port = port;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(loggerFactory));

        builder.WebHost.UseUrls($"http://localhost:{port}");

        builder.Services.AddSingleton(holder);
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(ChatController).Assembly);

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
        app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Chat}/{action=Index}/{id?}");

        await app.StartAsync(cancellationToken);

        ILogger logger = loggerFactory.CreateLogger("ARI.WebPanel");
        logger.LogInformation("Web panel is ready. Listening on http://localhost:{Port}", port);
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

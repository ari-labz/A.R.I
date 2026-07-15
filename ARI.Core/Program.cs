using ARI.Common;
using ARI.Core;
using ARI.Core.Scripts;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Templates;

string logPath = Path.Combine(Paths.Logs, "ARI.log");
Shared.LogPath = logPath;

if (File.Exists(logPath))
    File.Delete(logPath);

// Level token after the timestamp — [WARN] for warnings, [ERROR] for errors/fatals, nothing
// otherwise — so the server console window can colour whole lines by level.
ExpressionTemplate logTemplate = new(
    "[{@t:HH:mm:ss}] {#if @l = 'Warning'}[WARN] {#else if @l = 'Error' or @l = 'Fatal'}[ERROR] {#end}[{ShortSourceContext}] {@m}\n{@x}");

// Strip "ARI." prefix from SourceContext for cleaner log output
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Filter.ByExcluding(e =>
        e.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? source) &&
        source.ToString().StartsWith("\"Microsoft"))
    .Enrich.With<ShortSourceContextEnricher>()
    .WriteTo.Console(logTemplate)
    .WriteTo.File(logTemplate, logPath)
    .CreateLogger();

IHost host = Host.CreateDefaultBuilder(args)
    .UseSerilog()
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<ARI.Core.ARI>();
    })
    .Build();

AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
{
    Log.Fatal("[FATAL] Unhandled exception: {Exception}", eventArgs.ExceptionObject);
    EmergencyShutdown();
};

await host.RunAsync();
return;

void EmergencyShutdown()
{
    try
    {
        Log.Information("Emergency shutdown complete.");
    }
    finally
    {
        Log.CloseAndFlush();
    }
}

class ShortSourceContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        string name = logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? val)
            ? val.ToString().Trim('"')
            : "";
        if (name.StartsWith("ARI.")) name = name[4..];
        logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("ShortSourceContext", name));
    }
}

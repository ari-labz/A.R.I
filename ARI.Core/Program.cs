using ARI.Core;
using ARI.Core.Scripts;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ARI.log");

if (File.Exists(logPath))
    File.Delete(logPath);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Filter.ByExcluding(e =>
        e.Properties.TryGetValue("SourceContext", out var source) &&
        source.ToString().StartsWith("\"Microsoft"))
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(logPath, outputTemplate: "[{Timestamp:HH:mm:ss}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

IHost host = Host.CreateDefaultBuilder(args)
    .UseSerilog()
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<AriHostService>();
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
        string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        AriConfig config = AriConfig.LoadFrom(Path.Combine(executableDirectory, "AriConfig.json"));
        Docker docker = new Docker(Path.Combine(executableDirectory, config.Docker.ComposePath));
        docker.StopContainers().GetAwaiter().GetResult();
        Log.Information("Emergency shutdown complete.");
    }
    catch (Exception ex)
    {
        Log.Fatal("Emergency shutdown failed: {Error}", ex.Message);
    }
    finally
    {
        Log.CloseAndFlush();
    }
}

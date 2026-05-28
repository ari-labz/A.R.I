using ARI.Core;
using ARI.Core.Scripts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<AriHostService>();
    })
    .Build();

AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
{
    Console.WriteLine($"[FATAL] Unhandled exception: {args.ExceptionObject}");
    EmergencyShutdown();
};

AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
{
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
        Docker docker = new Docker(Path.Combine(executableDirectory, config.Docker.ComposePath), config.LLM.Endpoint);
        docker.StopContainers().GetAwaiter().GetResult();
        Console.WriteLine("Emergency shutdown complete.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FATAL] Emergency shutdown failed: {ex.Message}");
    }
}
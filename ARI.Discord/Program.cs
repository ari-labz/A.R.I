using ARI.Discord;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.Configure<DiscordConfig>(
            context.Configuration.GetSection("Discord"));

        services.AddHostedService<DiscordModule>();
    })
    .Build();

await host.RunAsync();
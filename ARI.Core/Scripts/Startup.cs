using System.Text.Json;
using ARI.Core.LLM;

namespace ARI.Core.Scripts;

public class Startup
{
    private readonly AriConfig config;

    public Startup()
    {
        string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        string appSettingsPath = Path.Combine(executableDirectory, "AriConfig.json");

        if (!File.Exists(appSettingsPath))
            throw new Exception("AriConfig.json file not found.");

        Console.WriteLine($"AriConfig.json found at {appSettingsPath}");

        string json = File.ReadAllText(appSettingsPath);

        AriConfig? deserialized = JsonSerializer.Deserialize<AriConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        config = deserialized ?? throw new Exception("Failed to deserialise AriConfig.json.");

        Console.WriteLine("AriConfig deserialised.");
    }

    public async Task StartAsync()
    {
        Console.WriteLine("ARI is starting...");

        await Dependency.CheckDocker();
        await Dependency.CheckPython();

        string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        string fullComposePath = Path.Combine(executableDirectory, config.Docker.ComposePath);


        Docker docker = new Docker(fullComposePath);
        try
        {
            await docker.IsRunning();
            await docker.StartContainers();

            Ollama ollama = new Ollama(config.LLM.Endpoint, config.LLM.Model);
            await ollama.IsInstalled();
            await ollama.ModelExists();

            LlmService llm = new LlmService(config.LLM.Endpoint, config.LLM.Model);
            string response = await llm.SendMessage("Say hello. Introduce yourself as ARI, a personal AI assistant.");
            Console.WriteLine(response);

            Console.WriteLine("ARI is ready. Press any key to shut down.");
            Console.ReadKey();
        }
        finally
        {
            Console.WriteLine("ARI is shutting down...");
            await docker.StopContainers();
        }
    }
}
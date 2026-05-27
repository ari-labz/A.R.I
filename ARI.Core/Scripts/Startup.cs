using System.ComponentModel.Design;
using System.Text.Json;

namespace ARI.Core.Scripts;

public class Startup
{
    private readonly AriConfig config;

    public Startup()
    {
        
        string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        string appSettingsPath = Path.Combine(executableDirectory, "AriConfig.json");
        if(!File.Exists(appSettingsPath))
            throw new FileNotFoundException("AriConfig.json file not found.");
        
        Console.WriteLine("AriConfig.json found at " + appSettingsPath);
        string json = File.ReadAllText(appSettingsPath);

        AriConfig? ariConfig = JsonSerializer.Deserialize<AriConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        config = ariConfig ?? throw new Exception("Failed to deserialise AriConfig.json.");
        
        Console.WriteLine("AriConfig deserialised.");
    }

    public async Task StartAsync()
    {
        Console.WriteLine("ARI is starting...");

        await Dependency.CheckDocker();
        await Dependency.CheckPython();

        Docker docker = new Docker(config.Docker.ComposePath);
        await docker.IsRunning();
        await docker.StartContainers();

        Ollama ollama = new Ollama(config.LLM.Model);
        await ollama.IsInstalled();
        await ollama.ModelExists();

        Console.WriteLine("ARI is ready.");
    }
}
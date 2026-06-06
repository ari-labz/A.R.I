using System.Text.Json;

namespace ARI.Core.Scripts;

public class AriLLMConfig
{
    public List<LlamaServerConfig> Servers { get; init; } = new();
    public List<LlamaModelConfig>  Models  { get; init; } = new();

    public static AriLLMConfig LoadFrom(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"AriLLMConfig.json not found at {path}");

        string json = File.ReadAllText(path);
        AriLLMConfig result = JsonSerializer.Deserialize<AriLLMConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Failed to deserialise AriLLMConfig.json.");

        foreach (LlamaModelConfig model in result.Models)
        {
            if (model.ServerIndex < 0 || model.ServerIndex >= result.Servers.Count)
                throw new InvalidOperationException(
                    $"Model '{model.File}' references ServerIndex {model.ServerIndex} but only {result.Servers.Count} server(s) are configured.");
        }

        return result;
    }
}

public class LlamaServerConfig
{
    public string Endpoint      { get; init; } = "";
    public int    Port          { get; init; } = 8081;
    public int    ContextSize   { get; init; } = 32768;
    public int    ParallelSlots { get; init; } = 1;
}

public class LlamaModelConfig
{
    public string File            { get; init; } = "";
    public string MmprojFile      { get; init; } = "";
    public bool   UseMtp          { get; init; } = false;
    public string ModelsPath      { get; init; } = "";
    public string DownloadBaseUrl { get; init; } = "";
    public int    ServerIndex     { get; init; } = 0;
}

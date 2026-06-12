namespace ARI.Core.Scripts;

public class AriLLMConfig
{
    public List<LlamaServerConfig> Servers { get; init; } = new();
    public List<LlamaModelConfig>  Models  { get; init; } = new();

    /// <summary>Verifies every model points at a server that exists.</summary>
    internal void Validate()
    {
        foreach (LlamaModelConfig model in Models)
        {
            if (model.ServerIndex < 0 || model.ServerIndex >= Servers.Count)
                throw new InvalidOperationException(
                    $"Model '{model.File}' references ServerIndex {model.ServerIndex} but only {Servers.Count} server(s) are configured.");
        }
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

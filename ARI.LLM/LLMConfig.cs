namespace ARI.LLM;

public class LLMConfig
{
    public bool Enabled { get; init; }
    public string                  ModelsPath { get; init; } = "Models";
    public List<LlamaServerConfig> Servers    { get; init; } = new();
}

public class LlamaServerConfig
{
    public string Name          { get; init; } = "";
    public string Endpoint      { get; init; } = "";
    public int    Port          { get; init; } = 8081;
    public int    ContextSize   { get; init; } = 32768;
    public int    ParallelSlots { get; init; } = 1;
    public bool   AutoStart     { get; init; } = true;
}

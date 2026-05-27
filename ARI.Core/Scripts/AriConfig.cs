namespace ARI.Core.Scripts;

public class AriConfig
{
    public LlmConfig LLM { get; init; }
    public TriliumConfig Trilium { get; init; }
    public DockerConfig Docker { get; init; }
}

public class LlmConfig
{
    public string Model { get; init; }
    public string Endpoint { get; init; }
}

public class TriliumConfig
{
    public string Endpoint { get; init; }
}

public class DockerConfig
{
    public string ComposePath { get; init; }
}
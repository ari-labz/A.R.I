namespace ARI.Core.Scripts;

public class AriLLMConfig
{
    public string                  ModelsPath { get; init; } = "Models";
    public List<LlamaServerConfig> Servers    { get; init; } = new();
}

public class LlamaServerConfig
{
    public string Name         { get; init; } = "";
    public string Endpoint     { get; init; } = "";
    public int    Port         { get; init; } = 8081;
    public int    ContextSize  { get; init; } = 32768;
    public int    ParallelSlots { get; init; } = 1;
    /// <summary>Model file path (relative to ModelsPath) to load on startup. Overridable at runtime via ModelSettingsStore.</summary>
    public string StartupModel { get; init; } = "";
}

/// <summary>
/// Runtime-only model descriptor built by ModelManager from disk scan.
/// Not deserialised from JSON.
/// </summary>
internal class LlamaModelConfig
{
    public string File            { get; init; } = "";
    public string MmprojFile      { get; init; } = "";
    public bool   UseMtp          { get; init; } = false;
    public string ModelsPath      { get; init; } = "";
    public string DownloadBaseUrl { get; init; } = "";

    public string EffectiveName => System.IO.Path.GetFileNameWithoutExtension(File);
}

namespace ARI.ImageGen;

public class ImageGenConfig
{
    public bool   Enabled          { get; init; }
    public string ComfyUiPath      { get; set; } = "";
    public int    Port             { get; init; } = 8188;
    public int    IdleSeconds      { get; init; } = 300;

    // Filename of the checkpoint inside ComfyUI's models/checkpoints/ folder.
    // Set by the user in AriConfig.json — never hardcoded in the repo.
    public string Checkpoint { get; init; } = "";
}

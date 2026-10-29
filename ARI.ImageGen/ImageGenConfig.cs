namespace ARI.ImageGen;

public class ImageGenConfig
{
    // Settable, not init: the control panel changes the checkpoint and the drawing defaults on the running
    // module, so a new model takes effect on the next image without a restart. The server settings
    // (path, port, remote URL) are read when ComfyUI starts, so those do wait for a restart.
    public bool   Enabled     { get; set; }
    public string ComfyUiPath { get; set; } = "";
    public int    Port        { get; set; } = 8188;
    public int    IdleSeconds { get; set; } = 300;

    // Set to use a ComfyUI that is already running (another machine, say) instead of the one ARI installs and
    // starts itself. Empty means ARI manages its own.
    public string RemoteUrl { get; set; } = "";

    // Filename of the checkpoint inside ComfyUI's models/checkpoints/ folder.
    // Set by the user in AriConfig.json — never hardcoded in the repo.
    public string Checkpoint { get; set; } = "";

    // How images are drawn unless a call asks for something else.
    public string Sampler   { get; set; } = "euler";
    public string Scheduler { get; set; } = "normal";
    public double Cfg       { get; set; } = 7.0;
    public int    Steps     { get; set; } = 25;
    public int    Width     { get; set; } = 1024;
    public int    Height    { get; set; } = 1024;

    public bool IsRemote => !string.IsNullOrWhiteSpace(RemoteUrl);
}

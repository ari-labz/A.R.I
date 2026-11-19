namespace ARI.Mesh;

public class MeshConfig
{
    // Settable, not init: the control panel changes these on the running module.
    public bool   Enabled { get; set; }

    // Where MeshServer (MeshServer/serve.py) answers, on this machine or another. Models are chosen and downloaded there.
    public string ServerUrl { get; set; } = "http://127.0.0.1:8199";

    // Stop the language-model servers while a mesh is made, then start them again. On by default: most models need the memory.
    public bool StopLlmsDuringJob { get; set; } = true;

    // Triangle budget for low-poly output.
    public int MaxFaces { get; set; } = 0;
}

using ARI.Common;
using System.Text.Json;

namespace ARI.LLM;

internal sealed class ImageToMesh : Tool
{
    private readonly Thread boundThread;

    internal ImageToMesh(Thread thread) => boundThread = thread;

    internal override string     Name   => "image_to_mesh";
    internal override ToolAccess Access => ToolAccess.Write;

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "image_to_mesh",
            description = "Turn an image into a low-poly 3D model (.glb) and save it to the scratchpad. Takes ONE image. If the " +
                          "user sent several views of the same object, pass the single clearest one (front-on, plain " +
                          "background, nothing else in frame); the others are not used. Takes one to three minutes. Tell the " +
                          "user in one short sentence before calling that it will take a few minutes, because the language " +
                          "models may be stopped while it runs, so you will go quiet. When it finishes, give the user the " +
                          "filename. The result is a rough low-poly asset, not an exact copy.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    image = new
                    {
                        type        = "string",
                        description = "Filename of the image in the scratchpad or project folder, e.g. \"axe.png\"."
                    },
                    max_faces = new
                    {
                        type        = "integer",
                        description = "Optional triangle limit. Leave out to keep the full mesh (the default), which is best for a starting point to retopologise by hand. Only set it when the user asks for a low-poly result: 2000 is a typical game prop."
                    },
                    format = new
                    {
                        type        = "string",
                        description = "\"glb\" (default) or \"fbx\" (texture embedded). Both are produced directly by this tool: when the user asks for fbx, pass \"fbx\" and do not convert anything yourself."
                    },
                    seed = new
                    {
                        type        = "integer",
                        description = "Random seed, to repeat a result or get a different one."
                    }
                },
                required = new[] { "image" }
            }
        }
    };

    internal override async Task<ToolResult> Execute(string args)
    {
        if (Modules.Mesh is null || !Modules.Mesh.IsReady)
            return "Mesh generation is not available. The Mesh module is switched off or has no server set.";

        using JsonDocument doc  = JsonDocument.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args);
        JsonElement        root = doc.RootElement;

        string name     = root.TryGetProperty("image",     out JsonElement im) ? im.GetString() ?? "" : "";
        int?   maxFaces = root.TryGetProperty("max_faces", out JsonElement mf) && mf.ValueKind == JsonValueKind.Number ? mf.GetInt32() : null;
        long?  seed     = root.TryGetProperty("seed",      out JsonElement sd) && sd.ValueKind == JsonValueKind.Number ? sd.GetInt64() : null;
        string format   = root.TryGetProperty("format",    out JsonElement fm) && string.Equals(fm.GetString(), "fbx", StringComparison.OrdinalIgnoreCase) ? "fbx" : "glb";
        if (string.IsNullOrWhiteSpace(name)) return "An image filename is required.";

        string fsRoot     = boundThread.FilesystemRoot ?? "";
        string scratchpad = Paths.ScratchpadDir(boundThread.Key);
        string path = Path.IsPathRooted(name) ? name
                    : File.Exists(Path.Combine(fsRoot, name)) ? Path.Combine(fsRoot, name)
                    : Path.Combine(scratchpad, name);
        if (!File.Exists(path)) return $"No image called {name} was found in the scratchpad or project folder.";

        IMeshModule mesh = Modules.Mesh;
        int faces = maxFaces ?? mesh.MaxFaces;
        if (faces != 0) faces = Math.Clamp(faces, 100, 200000);

        try
        {
            byte[] image = await File.ReadAllBytesAsync(path);
            byte[] glb   = await LlmPause.Around(mesh.StopLlmsDuringJob, "making a 3D model", 6, () => mesh.GenerateAsync(image, faces, seed, format));

            string dir = Path.GetDirectoryName(path)!;
            string filename = $"{Path.GetFileNameWithoutExtension(path)}-mesh-{DateTime.UtcNow:HHmmss}.{format}";
            await File.WriteAllBytesAsync(Path.Combine(dir, filename), glb);
            boundThread.FilesystemRoot ??= dir;
            return $"[saved: {filename}] {glb.Length / 1024} KB, {(faces == 0 ? "full mesh" : $"about {faces} triangles")}. Tell the user the filename. It opens in Blender.";
        }
        catch (Exception ex)
        {
            return $"Mesh generation failed: {ex.Message}";
        }
    }
}

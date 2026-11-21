using System.Text.Json;
using ARI.Common;

namespace ARI.LLM;

/// <summary>Lets Ari look at a 3D model the way a person turns it in a viewer: a .glb, .gltf (with its data inside), .obj, .stl or .fbx in the
/// working folder is drawn from several angles and comes back as one labelled sheet, with its triangle count and size.</summary>
internal sealed class PreviewModel : Tool
{
    private readonly Thread thread;
    internal PreviewModel(Thread thread) => this.thread = thread;

    internal override string Name => "preview_model";

    private static readonly string[] Kinds = ["glb", "gltf", "obj", "stl", "fbx"];
    private const long MAX_BYTES = 40 * 1024 * 1024;

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "preview_model",
            description = "Look at a 3D model: draws a .glb, .gltf (self-contained), .obj, .stl or .fbx from your working folder from several angles and returns one image of them, " +
                          "plus its triangle count and size. Use it to check a model you made or were given: shape, proportions, holes, topology (wireframe: true).",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    file      = new { type = "string",  description = "The model file in your working folder." },
                    views     = new { type = "string",  description = "Which angles: \"turnaround\" (front, right, back, left), \"six\" (those plus top and a three-quarter view, the default), or your own as yaw:pitch pairs in degrees, e.g. \"0:0,45:30,180:-20\"." },
                    wireframe = new { type = "boolean", description = "Draw edges only, to see the topology." },
                    size      = new { type = "integer", description = "Size of each view in px (default 400)." },
                },
                required = new[] { "file" }
            }
        }
    };

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        JsonElement root = doc.RootElement;
        string Str(string k) => root.TryGetProperty(k, out JsonElement v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

        string file = Str("file");
        string folder = thread.FilesystemRoot is { Length: > 0 } r ? r : Paths.ScratchpadDir(thread.Key);
        string full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(folder, file));
        if (!File.Exists(full)) return (ToolResult)$"[Error: {file} isn't in your working folder.]";
        string kind = Path.GetExtension(full).TrimStart('.').ToLowerInvariant();
        if (!Kinds.Contains(kind)) return (ToolResult)$"[Error: preview_model draws {string.Join(", ", Kinds.Select(k => "." + k))} files.]";
        if (new FileInfo(full).Length > MAX_BYTES) return (ToolResult)"[Error: that model is over 40 MB. Decimate it or export a lighter copy to look at.]";

        List<(int, int, string)> views = Views(Str("views"));
        if (views.Count == 0) return (ToolResult)"[Error: views is turnaround, six, or yaw:pitch pairs like 0:0,90:20.]";
        bool wire = Str("wireframe").Equals("true", StringComparison.OrdinalIgnoreCase);
        int size = int.TryParse(Str("size"), out int sz) ? Math.Clamp(sz, 160, 900) : 400;

        (byte[]? sheet, string facts, List<string> errors) = await BrowserHost.RenderModel(File.ReadAllBytes(full), kind, views, wire, size);
        if (sheet is null) return (ToolResult)$"[Error: couldn't draw {Path.GetFileName(full)}: {string.Join("; ", errors)}]";
        return ToolResult.AsImage(sheet, "image/png", $"{Path.GetFileName(full)} from {views.Count} angles{(wire ? ", wireframe" : "")}. Facts: {facts}." +
                                                     (errors.Count > 0 ? $" Warnings: {string.Join("; ", errors)}." : ""));
    }

    private static List<(int, int, string)> Views(string spec)
    {
        List<(int, int, string)> turnaround = [(0, 0, "front"), (90, 0, "right"), (180, 0, "back"), (270, 0, "left")];
        switch (spec.Trim().ToLowerInvariant())
        {
            case "turnaround": return turnaround;
            case "" or "six": return [.. turnaround, (0, 89, "top"), (35, 25, "three-quarter")];
        }
        List<(int, int, string)> own = new();
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(12))
        {
            string[] yp = part.Split(':');
            if (yp.Length != 2 || !int.TryParse(yp[0], out int yaw) || !int.TryParse(yp[1], out int pitch)) return new();
            own.Add((yaw, Math.Clamp(pitch, -89, 89), $"yaw {yaw}°, pitch {pitch}°"));
        }
        return own;
    }
}

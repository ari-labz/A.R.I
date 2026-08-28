using ARI.Common;
using System.Text.Json;
using System;

namespace ARI.LLM;

internal sealed class GenerateImage : Tool
{
    private string? _savedFilename;
    private string? _savedThreadKey;

    internal override string     Name   => "generate_image";
    internal override ToolAccess Access => ToolAccess.Write;

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "generate_image",
            description = "Generate an image from a text prompt using Stable Diffusion via ComfyUI. " +
                          "Returns the image directly into the conversation. " +
                          "Include a negative_prompt to exclude common artifacts (e.g. \"blurry, bad anatomy, watermark\"). " +
                          "Only call this when the user explicitly asks for an image to be generated or drawn. " +
                          "IMPORTANT: Never write 'Generating image...' or similar text — just call this tool and the image will appear automatically.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    prompt = new
                    {
                        type        = "string",
                        description = "What to draw. Be descriptive — include subject, setting, lighting, and mood."
                    },
                    negative_prompt = new
                    {
                        type        = "string",
                        description = "What to avoid. Common values: \"blurry, bad anatomy, bad hands, watermark, low quality\"."
                    },
                    width = new
                    {
                        type        = "integer",
                        description = "Image width in pixels. Defaults to 1024. Use multiples of 64."
                    },
                    height = new
                    {
                        type        = "integer",
                        description = "Image height in pixels. Defaults to 1024. Use multiples of 64."
                    },
                    steps = new
                    {
                        type        = "integer",
                        description = "Denoising steps. Higher = better quality but slower. Defaults to 25. Range: 10–50."
                    }
                },
                required = new[] { "prompt" }
            }
        }
    };


    internal override async Task<ToolResult> Execute(string args)
    {
        if (Modules.ImageGen is null)
            return "Image generation is not available — the ImageGen module is not enabled.";

        if (!Modules.ImageGen.IsReady)
            return "Image generation is not ready — ComfyUI failed to install. Check the server logs.";

        using JsonDocument doc  = JsonDocument.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args);
        JsonElement        root = doc.RootElement;

        string prompt         = root.TryGetProperty("prompt",          out JsonElement pr) ? pr.GetString() ?? "" : "";
        string negativePrompt = root.TryGetProperty("negative_prompt", out JsonElement np) ? np.GetString() ?? "" : "";
        int    width          = root.TryGetProperty("width",           out JsonElement w)  ? w.GetInt32()  : 1024;
        int    height         = root.TryGetProperty("height",          out JsonElement h)  ? h.GetInt32()  : 1024;
        int    steps          = root.TryGetProperty("steps",           out JsonElement sp) ? sp.GetInt32() : 25;

        if (string.IsNullOrWhiteSpace(prompt))
            return "A prompt is required to generate an image.";

        try
        {
            byte[] imageBytes = await Modules.ImageGen.GenerateAsync(
                prompt, negativePrompt, steps: steps, width: width, height: height);
            return ToolResult.AsImage(imageBytes, "image/png");
        }
        catch (Exception ex)
        {
            return $"Image generation failed: {ex.Message}";
        }
    }

    internal override ToolResult PostRun(Thread thread, string argsJson, ToolResult result)
    {
        if (result.Kind != ToolResult.ContentKind.Image) return result;

        string dir      = Paths.ScratchpadDir(thread.Key);
        Directory.CreateDirectory(dir);
        string filename = $"ari-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png";
        File.WriteAllBytes(Path.Combine(dir, filename), result.Bytes);

        // Ensure thread cleanup will delete the scratchpad on death.
        thread.FilesystemRoot ??= dir;

        _savedFilename  = filename;
        _savedThreadKey = thread.Key;

        string url = $"/threads/{Uri.EscapeDataString(thread.Key)}/scratchpad/{Uri.EscapeDataString(filename)}";
        thread.RaiseScratchpadFileReady(url);

        return result;
    }

    internal override Func<string, string>? DisplayAfter => _ =>
        _savedFilename is not null && _savedThreadKey is not null
            ? $"\n<!--ari-image:{_savedThreadKey}:{_savedFilename}-->"
            : "";
}

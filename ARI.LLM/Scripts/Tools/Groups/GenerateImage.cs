using ARI.Common;
using System.Text.Json;
using System;

namespace ARI.LLM;

internal sealed class GenerateImage : Tool
{
    private readonly Thread boundThread;

    internal GenerateImage(Thread thread) => boundThread = thread;

    internal override string     Name   => "generate_image";
    internal override ToolAccess Access => ToolAccess.Write;

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "generate_image",
            description = "Generate an image from a text prompt using Stable Diffusion via ComfyUI, and save it to " +
                          "the scratchpad. You will see the result yourself before the user does — it is saved to disk " +
                          "but NOT shown to them automatically. After generating, judge it against every concrete detail " +
                          "in your prompt (subject, count, pose, specific features, setting, style) — not just 'does this " +
                          "vaguely match the vibe'. A wrong detail (wrong gender presentation, wrong number of subjects, " +
                          "missing a specific feature you asked for, mangled anatomy, a stray artifact) means it is NOT a " +
                          "match, however close it looks otherwise. Only call present_image with the saved filename once " +
                          "you can point to specifically why it matches; otherwise call generate_image again with a " +
                          "revised prompt, or tell the user you're having trouble if repeated attempts fail. Never present " +
                          "an image you haven't actually looked at, and never present one just because you're tired of " +
                          "retrying. Include a negative_prompt to exclude common artifacts (e.g. \"blurry, bad anatomy, watermark\"). " +
                          "Only call this when the user explicitly asks for an image to be generated or drawn. " +
                          "If the user attached reference images to their message, pass their filenames via reference_images — they will be used as img2img references. " +
                          "If you do retry, name the specific detail that was wrong and change ONE thing to fix it — " +
                          "don't rewrite the whole prompt or fire off a string of near-identical guesses; each call costs " +
                          "real generation time. If a reference image is being used for a character or scene, keep the " +
                          "same reference_images and denoise across retries so the identity doesn't drift. " +
                          "IMPORTANT: Never write 'Generating image...' or similar text before calling this. And never " +
                          "narrate a rejection or a retry to the user — they never saw the bad image, so 'the mouth was " +
                          "mangled, let me redo it' means nothing to them and just reads as rambling. Reject and retry " +
                          "silently: call generate_image again with no reply text in between. Only write a reply once " +
                          "you present_image or give up — and when you do, talk about the image the user actually sees, not the ones before it.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    prompt = new
                    {
                        type        = "string",
                        description = "What to draw. Order matters more than length: lead with the main subject, " +
                                      "then its pose/action, then the setting, then lighting, then art style, then mood. " +
                                      "Concrete and specific beats long and vague — name colors, materials, and camera " +
                                      "framing rather than adjectives like 'beautiful' or 'detailed'."
                    },
                    negative_prompt = new
                    {
                        type        = "string",
                        description = "What to avoid. Common values: \"blurry, bad anatomy, bad hands, watermark, low quality\"."
                    },
                    reference_images = new
                    {
                        type        = "array",
                        items       = new { type = "string" },
                        description = "Filenames of images in the scratchpad to use as img2img references (e.g. [\"eris.png\"]). The first image sets the composition; additional images are noted but only the first is used as the latent init. Use when the user has attached reference images."
                    },
                    denoise = new
                    {
                        type        = "number",
                        description = "How much to deviate from the reference image(s). 1.0 = ignore reference (pure txt2img), 0.5 = halfway between reference and prompt, 0.3 = stay close to reference. Defaults to 0.75 when reference images are provided."
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

        string  prompt         = root.TryGetProperty("prompt",           out JsonElement pr) ? pr.GetString() ?? "" : "";
        string  negativePrompt = root.TryGetProperty("negative_prompt",  out JsonElement np) ? np.GetString() ?? "" : "";
        int     width          = root.TryGetProperty("width",            out JsonElement w)  ? w.GetInt32()  : 1024;
        int     height         = root.TryGetProperty("height",           out JsonElement h)  ? h.GetInt32()  : 1024;
        int     steps          = root.TryGetProperty("steps",            out JsonElement sp) ? sp.GetInt32() : 25;
        float   denoise        = root.TryGetProperty("denoise",          out JsonElement dn) ? (float)dn.GetDouble() : -1f;

        // Resolve reference images: filenames → absolute scratchpad paths
        string[] referenceImages = [];
        if (root.TryGetProperty("reference_images", out JsonElement ri) && ri.ValueKind == JsonValueKind.Array)
        {
            string scratchpad = boundThread?.FilesystemRoot ?? "";
            referenceImages = ri.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => Path.IsPathRooted(name) ? name : Path.Combine(scratchpad, name))
                .Where(File.Exists)
                .ToArray();
        }

        // Default denoise for img2img: 0.75 (keeps character, allows prompt variation)
        if (denoise < 0) denoise = referenceImages.Length > 0 ? 0.75f : 1.0f;

        if (string.IsNullOrWhiteSpace(prompt))
            return "A prompt is required to generate an image.";

        try
        {
            byte[] imageBytes = await Modules.ImageGen.GenerateAsync(
                prompt, negativePrompt,
                steps: steps, width: width, height: height,
                referenceImages: referenceImages, denoise: denoise);

            string dir = Paths.ScratchpadDir(boundThread.Key);
            Directory.CreateDirectory(dir);
            string filename = $"ari-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png";
            File.WriteAllBytes(Path.Combine(dir, filename), imageBytes);

            // Ensure thread cleanup will delete the scratchpad on death.
            boundThread.FilesystemRoot ??= dir;

            string note = $"[saved: {filename}] This has NOT been shown to the user yet. Look at it above and check it " +
                          $"against every concrete detail in the prompt you just sent — \"{prompt}\" — not just whether it " +
                          "looks roughly on-theme. If anything specific is wrong (wrong gender presentation, wrong subject " +
                          "count, a missing or wrong feature, mangled anatomy, an artifact), call generate_image again with " +
                          "that one thing fixed — no reply text, just the tool call; the user never saw this one so there's " +
                          $"nothing to explain. Only call present_image with filename=\"{filename}\" once you can name " +
                          "specifically why it matches what was asked for.";
            return ToolResult.AsImage(imageBytes, "image/png", visionNote: note);
        }
        catch (Exception ex)
        {
            return $"Image generation failed: {ex.Message}";
        }
    }
}

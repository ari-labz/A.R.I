using ARI.Common;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARI.ImageGen;

public class ImageGenModule : IImageGenModule
{
    private readonly ImageGenConfig _config;
    private readonly object         _lock    = new();
    private Process?                _process = null;
    private Timer?                  _idleTimer;

    public bool IsReady => Dependency.Status == "";

    public ImageGenModule(ImageGenConfig config)
    {
        _config = config;
    }

    public async Task<byte[]> GenerateAsync(
        string   prompt,
        string   negativePrompt     = "",
        string   checkpointFilename = "",
        int      steps              = 25,
        int      width              = 1024,
        int      height             = 1024,
        long     seed               = -1,
        string[] referenceImages    = default!,
        float    denoise            = 1.0f,
        CancellationToken ct        = default)
    {
        await EnsureRunning(ct);
        ResetIdleTimer();

        if (seed == -1) seed = Random.Shared.NextInt64(0, long.MaxValue);

        if (string.IsNullOrWhiteSpace(checkpointFilename))
            checkpointFilename = _config.Checkpoint;

        referenceImages ??= [];

        // Upload reference images to ComfyUI's input directory before building the workflow.
        string baseUrl = $"http://127.0.0.1:{_config.Port}";
        List<string> uploadedNames = new();
        using HttpClient hc = new();
        foreach (string path in referenceImages)
        {
            if (!File.Exists(path)) continue;
            using MultipartFormDataContent form = new();
            byte[] imgBytes = await File.ReadAllBytesAsync(path, ct);
            form.Add(new ByteArrayContent(imgBytes), "image", Path.GetFileName(path));
            form.Add(new StringContent("true"), "overwrite");
            HttpResponseMessage upResp = await hc.PostAsync($"{baseUrl}/upload/image", form, ct);
            if (upResp.IsSuccessStatusCode)
            {
                JsonNode? upJson = JsonNode.Parse(await upResp.Content.ReadAsStringAsync(ct));
                string? uploadedName = upJson?["name"]?.GetValue<string>();
                if (uploadedName is not null) uploadedNames.Add(uploadedName);
            }
        }

        string workflow = uploadedNames.Count > 0
            ? BuildImg2ImgWorkflow(prompt, negativePrompt, checkpointFilename, steps, width, height, seed, uploadedNames[0], denoise)
            : BuildWorkflow(prompt, negativePrompt, checkpointFilename, steps, width, height, seed);

        // Submit the prompt
        HttpResponseMessage submitResp = await hc.PostAsync(
            $"{baseUrl}/prompt",
            new StringContent(workflow, System.Text.Encoding.UTF8, "application/json"),
            ct);
        if (!submitResp.IsSuccessStatusCode)
        {
            string body = await submitResp.Content.ReadAsStringAsync(ct);
            throw new Exception($"ComfyUI rejected workflow ({(int)submitResp.StatusCode}): {body}");
        }

        JsonNode? submitJson = JsonNode.Parse(await submitResp.Content.ReadAsStringAsync(ct));
        string promptId = submitJson?["prompt_id"]?.GetValue<string>()
            ?? throw new Exception("ComfyUI did not return a prompt_id.");

        // Poll history until the prompt completes
        int consecutiveErrors = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);

            try
            {
                HttpResponseMessage histResp = await hc.GetAsync($"{baseUrl}/history/{promptId}", ct);
                histResp.EnsureSuccessStatusCode();

                JsonNode? history = JsonNode.Parse(await histResp.Content.ReadAsStringAsync(ct));
                JsonNode? entry   = history?[promptId];
                if (entry is null) { consecutiveErrors = 0; continue; }

                // Detect ComfyUI execution errors (e.g. BrokenPipeError from MPS OOM)
                string? statusStr = entry["status"]?["status_str"]?.GetValue<string>();
                if (statusStr == "error")
                {
                    JsonNode? msgs = entry["status"]?["messages"];
                    string errMsg = "unknown error";
                    if (msgs is not null)
                        foreach (JsonNode? msg in msgs.AsArray())
                            if (msg?[0]?.GetValue<string>() == "execution_error")
                            {
                                errMsg = msg[1]?["exception_message"]?.GetValue<string>() ?? errMsg;
                                break;
                            }
                    throw new Exception($"ComfyUI job failed: {errMsg}");
                }

                JsonNode? outputs = entry["outputs"];
                if (outputs is null) { consecutiveErrors = 0; continue; }

                // Find the first image output across all nodes
                foreach (JsonNode? node in outputs.AsObject().Select(kv => kv.Value))
                {
                    JsonNode? images = node?["images"];
                    if (images is null) continue;

                    JsonNode? first = images.AsArray().FirstOrDefault();
                    if (first is null) continue;

                    string filename  = first["filename"]!.GetValue<string>();
                    string subfolder = first["subfolder"]?.GetValue<string>() ?? "";
                    string type      = first["type"]?.GetValue<string>() ?? "output";

                    string imageUrl = $"{baseUrl}/view?filename={Uri.EscapeDataString(filename)}"
                        + $"&subfolder={Uri.EscapeDataString(subfolder)}&type={type}";

                    return await hc.GetByteArrayAsync(imageUrl, ct);
                }
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                consecutiveErrors++;
                Shared.Logger.LogWarning("[ImageGen] Poll error ({Count}): {Msg}", consecutiveErrors, ex.Message);
                // If ComfyUI has crashed (10 consecutive failures), give up rather than hanging forever.
                if (consecutiveErrors >= 10)
                    throw new Exception($"ComfyUI stopped responding after {consecutiveErrors} consecutive poll failures: {ex.Message}", ex);
            }
        }

        throw new OperationCanceledException(ct);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private async Task EnsureRunning(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_process is { HasExited: false }) return;
        }

        await StartComfyUi(ct);
    }

    private async Task StartComfyUi(CancellationToken ct)
    {
        string installDir2 = Dependency.ComfyUiPath ?? Dependency.DefaultInstallPath();
        string? python = Dependency.VenvPython(installDir2) ?? await FindPython();
        if (python is null)
            throw new Exception("Python 3 not found — cannot start ComfyUI.");

        string installDir = Dependency.ComfyUiPath
            ?? Dependency.DefaultInstallPath();
        string mainScript = Path.Combine(installDir, "main.py");

        if (!File.Exists(mainScript))
            throw new Exception($"ComfyUI main.py not found at {mainScript}. Is it installed?");

        ProcessStartInfo psi = new(python, $"\"{mainScript}\" --port {_config.Port} --listen 127.0.0.1 --preview-method none")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = installDir,
        };

        Process process = Process.Start(psi)!;
        // Drain stdout/stderr continuously so ComfyUI's logger never blocks on a full pipe.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived  += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_lock) _process = process;

        Shared.Logger.LogInformation("[ImageGen] ComfyUI starting on port {Port}...", _config.Port);

        // Wait until the HTTP server is ready
        using HttpClient hc = new() { Timeout = TimeSpan.FromSeconds(2) };
        string url = $"http://127.0.0.1:{_config.Port}";
        Stopwatch sw = Stopwatch.StartNew();

        while (sw.Elapsed < TimeSpan.FromSeconds(60) && !ct.IsCancellationRequested)
        {
            try
            {
                HttpResponseMessage r = await hc.GetAsync(url, ct);
                if (r.IsSuccessStatusCode) break;
            }
            catch { }
            await Task.Delay(500, ct);
        }

        Shared.Logger.LogInformation("[ImageGen] ComfyUI ready.");
    }

    public void Shutdown()
    {
        lock (_lock)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;

            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                Shared.Logger.LogInformation("[ImageGen] ComfyUI stopped.");
            }
            _process = null;
        }
    }

    private void ResetIdleTimer()
    {
        lock (_lock)
        {
            _idleTimer?.Dispose();
            _idleTimer = new Timer(_ => Shutdown(), null,
                TimeSpan.FromSeconds(_config.IdleSeconds),
                Timeout.InfiniteTimeSpan);
        }
    }

    // ── Workflow builder ──────────────────────────────────────────────────────

    private static string BuildWorkflow(
        string prompt, string negativePrompt, string checkpoint,
        int steps, int width, int height, long seed)
    {
        // Standard KSampler text-to-image workflow.
        // Node IDs are arbitrary stable strings ComfyUI uses to wire inputs/outputs.
        JsonObject workflow = new()
        {
            ["1"] = new JsonObject
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"]     = new JsonObject { ["ckpt_name"] = checkpoint }
            },
            ["2"] = new JsonObject
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"]     = new JsonObject
                {
                    ["text"] = prompt,
                    ["clip"] = new JsonArray { "1", 1 }
                }
            },
            ["3"] = new JsonObject
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"]     = new JsonObject
                {
                    ["text"] = negativePrompt,
                    ["clip"] = new JsonArray { "1", 1 }
                }
            },
            ["4"] = new JsonObject
            {
                ["class_type"] = "EmptyLatentImage",
                ["inputs"]     = new JsonObject { ["width"] = width, ["height"] = height, ["batch_size"] = 1 }
            },
            ["5"] = new JsonObject
            {
                ["class_type"] = "KSampler",
                ["inputs"]     = new JsonObject
                {
                    ["model"]          = new JsonArray { "1", 0 },
                    ["positive"]       = new JsonArray { "2", 0 },
                    ["negative"]       = new JsonArray { "3", 0 },
                    ["latent_image"]   = new JsonArray { "4", 0 },
                    ["seed"]           = seed,
                    ["steps"]          = steps,
                    ["cfg"]            = 7.0,
                    ["sampler_name"]   = "euler",
                    ["scheduler"]      = "normal",
                    ["denoise"]        = 1.0
                }
            },
            ["6"] = new JsonObject
            {
                ["class_type"] = "VAEDecode",
                ["inputs"]     = new JsonObject
                {
                    ["samples"] = new JsonArray { "5", 0 },
                    ["vae"]     = new JsonArray { "1", 2 }
                }
            },
            ["7"] = new JsonObject
            {
                ["class_type"] = "SaveImage",
                ["inputs"]     = new JsonObject
                {
                    ["images"]         = new JsonArray { "6", 0 },
                    ["filename_prefix"] = "ari"
                }
            }
        };

        return JsonSerializer.Serialize(new JsonObject { ["prompt"] = workflow });
    }

    // Standard img2img: load reference image → VAE encode → KSampler with denoise < 1 → SaveImage.
    private static string BuildImg2ImgWorkflow(
        string prompt, string negativePrompt, string checkpoint,
        int steps, int width, int height, long seed,
        string referenceImageName, float denoise)
    {
        JsonObject workflow = new()
        {
            ["1"] = new JsonObject
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"]     = new JsonObject { ["ckpt_name"] = checkpoint }
            },
            ["2"] = new JsonObject
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"]     = new JsonObject
                {
                    ["text"] = prompt,
                    ["clip"] = new JsonArray { "1", 1 }
                }
            },
            ["3"] = new JsonObject
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"]     = new JsonObject
                {
                    ["text"] = negativePrompt,
                    ["clip"] = new JsonArray { "1", 1 }
                }
            },
            // Load the reference image
            ["8"] = new JsonObject
            {
                ["class_type"] = "LoadImage",
                ["inputs"]     = new JsonObject { ["image"] = referenceImageName, ["upload"] = "image" }
            },
            // Resize to target resolution before encoding
            ["9"] = new JsonObject
            {
                ["class_type"] = "ImageScale",
                ["inputs"]     = new JsonObject
                {
                    ["image"]          = new JsonArray { "8", 0 },
                    ["upscale_method"] = "lanczos",
                    ["width"]          = width,
                    ["height"]         = height,
                    ["crop"]           = "center"
                }
            },
            // VAE encode to latent
            ["10"] = new JsonObject
            {
                ["class_type"] = "VAEEncode",
                ["inputs"]     = new JsonObject
                {
                    ["pixels"] = new JsonArray { "9", 0 },
                    ["vae"]    = new JsonArray { "1", 2 }
                }
            },
            ["5"] = new JsonObject
            {
                ["class_type"] = "KSampler",
                ["inputs"]     = new JsonObject
                {
                    ["model"]          = new JsonArray { "1", 0 },
                    ["positive"]       = new JsonArray { "2", 0 },
                    ["negative"]       = new JsonArray { "3", 0 },
                    ["latent_image"]   = new JsonArray { "10", 0 },
                    ["seed"]           = seed,
                    ["steps"]          = steps,
                    ["cfg"]            = 7.0,
                    ["sampler_name"]   = "euler",
                    ["scheduler"]      = "normal",
                    ["denoise"]        = (double)denoise
                }
            },
            ["6"] = new JsonObject
            {
                ["class_type"] = "VAEDecode",
                ["inputs"]     = new JsonObject
                {
                    ["samples"] = new JsonArray { "5", 0 },
                    ["vae"]     = new JsonArray { "1", 2 }
                }
            },
            ["7"] = new JsonObject
            {
                ["class_type"] = "SaveImage",
                ["inputs"]     = new JsonObject
                {
                    ["images"]          = new JsonArray { "6", 0 },
                    ["filename_prefix"] = "ari-img2img"
                }
            }
        };

        return JsonSerializer.Serialize(new JsonObject { ["prompt"] = workflow });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string?> FindPython()
    {
        foreach (string candidate in new[] { "python3", "python" })
        {
            try
            {
                Process p = Process.Start(new ProcessStartInfo(candidate, "--version")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                })!;
                await p.WaitForExitAsync();
                if (p.ExitCode == 0) return candidate;
            }
            catch { }
        }
        return null;
    }
}

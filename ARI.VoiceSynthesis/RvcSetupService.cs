using System.Diagnostics;

namespace ARI.VoiceSynthesis;

/// <summary>
/// Ensures RVC's Python dependencies and model weights are present.
/// RVC source now lives in the repo (External/RVC) with all patches already applied —
/// this service only handles pip packages, model downloads, and site-package patches.
/// </summary>
public class RvcSetupService
{
    private const string PythonPath = "/opt/homebrew/bin/python3.11";
    private const string MarkerFile = ".ari_installed";

    private readonly string rvcPath;
    private readonly string voicesPath;

    public RvcSetupService(string rvcPath, string voicesPath)
    {
        this.rvcPath    = rvcPath;
        this.voicesPath = voicesPath;
    }

    public async Task InstallAsync()
    {
        if (!Directory.Exists(rvcPath))
            throw new DirectoryNotFoundException(
                $"RVC directory not found at '{rvcPath}'. " +
                "If you deleted it, restore it from git: git checkout External/RVC");

        Directory.CreateDirectory(voicesPath);

        bool needsInstall = !File.Exists(Path.Combine(rvcPath, MarkerFile));

        if (needsInstall)
        {
            await InstallDependenciesAsync();
            File.WriteAllText(Path.Combine(rvcPath, MarkerFile), DateTime.UtcNow.ToString("O"));
            Log("Installation complete.");
        }
        else
        {
            Log("Dependencies already installed.");
        }

        // Always re-run: these patch site-packages (gradio, fairseq) which may be
        // reinstalled by pip. RVC source patches are baked into the repo files.
        WriteEnvFile();
        PatchFairseqDataclassConfigs();
        PatchGradio();
        Log("Ready.");
    }

    // ── pip + model install ───────────────────────────────────────────────────

    private async Task InstallDependenciesAsync()
    {
        Log("Installing Python dependencies (first run only — this may take several minutes)...");
        await RunAsync(PythonPath, "-m pip install -r requirements.txt", rvcPath);

        // fairseq 0.12.2 on PyPI has an incomplete source tarball; install from git tag instead.
        // --no-deps bypasses the omegaconf<2.1 constraint that modern pip cannot resolve.
        Log("Installing fairseq from git (no-deps to bypass omegaconf metadata bug)...");
        await RunAsync(PythonPath,
            "-m pip install \"git+https://github.com/facebookresearch/fairseq.git@v0.12.2\" --no-deps",
            rvcPath);

        PatchFairseqDataclassConfigs();

        Log("Pinning faiss-cpu to compatible version...");
        await RunAsync(PythonPath, "-m pip install \"faiss-cpu==1.8.0.post1\" --force-reinstall", rvcPath);

        await DownloadRequiredModelsAsync();
        Log("Dependencies installed.");
    }

    private async Task DownloadRequiredModelsAsync()
    {
        await DownloadIfMissingAsync(
            url:       "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/main/hubert_base.pt",
            localPath: Path.Combine(rvcPath, "assets", "hubert", "hubert_base.pt"),
            label:     "HuBERT base model");

        await DownloadIfMissingAsync(
            url:       "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/main/rmvpe.pt",
            localPath: Path.Combine(rvcPath, "assets", "rmvpe", "rmvpe.pt"),
            label:     "rmvpe pitch model");

        await DownloadIfMissingAsync(
            url:       "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/main/pretrained_v2/f0G40k.pth",
            localPath: Path.Combine(rvcPath, "assets", "pretrained_v2", "f0G40k.pth"),
            label:     "pretrained generator (f0G40k)");

        await DownloadIfMissingAsync(
            url:       "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/main/pretrained_v2/f0D40k.pth",
            localPath: Path.Combine(rvcPath, "assets", "pretrained_v2", "f0D40k.pth"),
            label:     "pretrained discriminator (f0D40k)");
    }

    private async Task DownloadIfMissingAsync(string url, string localPath, string label)
    {
        if (File.Exists(localPath)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        Log($"Downloading {label}...");
        using var client = new System.Net.Http.HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        using var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var file   = File.Create(localPath);
        await stream.CopyToAsync(file);
        Log($"{label} downloaded.");
    }

    // ── .env ─────────────────────────────────────────────────────────────────

    private void WriteEnvFile()
    {
        string content =
            $"OPENBLAS_NUM_THREADS = 1{NewLine}" +
            $"OMP_NUM_THREADS = 1{NewLine}" +
            $"MKL_NUM_THREADS = 1{NewLine}" +
            $"PYTORCH_ENABLE_MPS_FALLBACK = 1{NewLine}" +
            $"RVC_FORCE_CPU = 1{NewLine}" +
            $"no_proxy = localhost, 127.0.0.1, ::1{NewLine}" +
            $"{NewLine}" +
            $"weight_root = {voicesPath}{NewLine}" +
            $"weight_uvr5_root = assets/uvr5_weights{NewLine}" +
            $"index_root = {voicesPath}{NewLine}" +
            $"outside_index_root = assets/indices{NewLine}" +
            $"rmvpe_root = assets/rmvpe{NewLine}";

        File.WriteAllText(Path.Combine(rvcPath, ".env"), content);
    }

    // ── Site-package patches (fairseq + gradio) ───────────────────────────────
    // These patch installed pip packages, not RVC source, so they must run every
    // time in case packages were updated/reinstalled.

    private void PatchFairseqDataclassConfigs()
    {
        string sitePackages = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(PythonPath)!, "..", "lib", "python3.11", "site-packages"));
        string fairseqDir = Path.Combine(sitePackages, "fairseq");

        if (!Directory.Exists(fairseqDir))
        {
            Log("Warning: could not locate fairseq package — skipping Python 3.11 dataclass patch.");
            return;
        }

        string scriptPath = Path.Combine(Path.GetTempPath(), "patch_fairseq.py");
        File.WriteAllText(scriptPath, """
import os, re, sys

fairseq_dir = sys.argv[1]
fixed_files = 0

for root, _, files in os.walk(fairseq_dir):
    for fname in files:
        if not fname.endswith('.py'):
            continue
        path = os.path.join(root, fname)
        c = open(path).read()
        orig = c

        c, n1 = re.subn(
            r'(\s)(\w+): ([A-Z]\w*) = ([A-Z]\w*)\(\)',
            r'\1\2: \3 = field(default_factory=\4)', c)

        c, n2 = re.subn(
            r'(\s)(\w+): ([A-Z]\w*) = field\(default=([A-Z]\w*)\(\)\)',
            r'\1\2: \3 = field(default_factory=\4)', c)

        if c != orig:
            if 'from dataclasses import' in c:
                c = re.sub(
                    r'(from dataclasses import )([^\n]+)',
                    lambda m: m.group(0) if 'field' in m.group(2)
                              else m.group(1) + m.group(2) + ', field',
                    c, count=1)
            open(path, 'w').write(c)
            fixed_files += 1

init_path = os.path.join(fairseq_dir, '__init__.py')
if os.path.exists(init_path):
    c = open(init_path).read()
    if 'hydra_init()' in c and 'try:\n    hydra_init()' not in c:
        c = c.replace('hydra_init()',
            'try:\n    hydra_init()\nexcept Exception:\n    pass  # omegaconf version mismatch')
        open(init_path, 'w').write(c)
        fixed_files += 1

ckpt_path = os.path.join(fairseq_dir, 'checkpoint_utils.py')
if os.path.exists(ckpt_path):
    c = open(ckpt_path).read()
    orig_ckpt = c
    c = c.replace(
        'torch.load(f, map_location=torch.device("cpu"))',
        'torch.load(f, map_location=torch.device("cpu"), weights_only=False)'
    )
    c = c.replace(
        'map_location=(\n                lambda s, _: torch.serialization.default_restore_location(s, "cpu")\n            ),\n        )',
        'map_location=(\n                lambda s, _: torch.serialization.default_restore_location(s, "cpu")\n            ),\n            weights_only=False,\n        )'
    )
    if c != orig_ckpt:
        open(ckpt_path, 'w').write(c)
        fixed_files += 1

print(f'Patched {fixed_files} fairseq file(s) for Python 3.11 / PyTorch 2.6 compatibility.')
""");

        Log("Patching fairseq for Python 3.11 compatibility...");
        RunAsync(PythonPath, $"\"{scriptPath}\" \"{fairseqDir}\"", rvcPath).GetAwaiter().GetResult();
        File.Delete(scriptPath);
    }

    private void PatchGradio()
    {
        string? gradioDir = FindGradioPackageDir();
        if (gradioDir is null)
        {
            Log("Warning: could not locate Gradio package — skipping Gradio timeout patches.");
            return;
        }

        PatchFile(
            path:    Path.Combine(gradioDir, "utils.py"),
            marker:  "timeout=300.0",
            oldText: "client = httpx.AsyncClient()",
            newText: "client = httpx.AsyncClient(timeout=300.0)"
        );

        PatchFile(
            path:    Path.Combine(gradioDir, "queueing.py"),
            marker:  "timeout=300.0",
            oldText: "self.queue_client = httpx.AsyncClient(verify=ssl_verify)",
            newText: "self.queue_client = httpx.AsyncClient(verify=ssl_verify, timeout=300.0)"
        );
    }

    private string? FindGradioPackageDir()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName               = PythonPath,
                Arguments              = "-c \"import gradio, os; print(os.path.dirname(gradio.__file__))\"",
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                WorkingDirectory       = rvcPath
            })!;

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return Directory.Exists(output) ? output : null;
        }
        catch { return null; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void PatchFile(string path, string marker, string oldText, string newText)
    {
        if (!File.Exists(path)) return;
        string content = File.ReadAllText(path);
        if (content.Contains(marker)) return;
        File.WriteAllText(path, content.Replace(oldText, newText));
    }

    private async Task RunAsync(string fileName, string arguments, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName         = fileName,
            Arguments        = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute  = false,
        }) ?? throw new Exception($"Failed to start: {fileName} {arguments}");

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new Exception($"Command exited with code {process.ExitCode}: {fileName} {arguments}");
    }

    private static void Log(string message) =>
        Console.WriteLine($"[VoiceSynthesis] {message}");

    private static string NewLine => "\n";
}

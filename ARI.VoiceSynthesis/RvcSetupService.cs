using System.Diagnostics;

namespace ARI.VoiceSynthesis;

public class RvcSetupService
{
    private const string PythonPath    = "/opt/homebrew/bin/python3.11";
    private const string RvcRepoUrl    = "https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI";
    private const string RvcCommit     = "7ef19867780cf703841ebafb565a4e47d1ea86ff";
    private const string MarkerFile    = ".ari_installed";

    private readonly string rvcPath;
    private readonly string voicesPath;

    public RvcSetupService(string rvcPath, string voicesPath)
    {
        this.rvcPath    = rvcPath;
        this.voicesPath = voicesPath;
    }

    public async Task InstallAsync()
    {
        Directory.CreateDirectory(voicesPath);
        Directory.CreateDirectory(Path.Combine(voicesPath));

        bool needsInstall = !File.Exists(Path.Combine(rvcPath, MarkerFile));

        if (needsInstall)
        {
            await CloneRvcAsync();
            await InstallDependenciesAsync();
            File.WriteAllText(Path.Combine(rvcPath, MarkerFile), DateTime.UtcNow.ToString("O"));
            Log("Installation complete.");
        }
        else
        {
            Log("Already installed, applying patches...");
        }

        // Always rewrite .env and re-apply patches — idempotent and cheap
        WriteEnvFile();
        ApplyPatches();
        Log("Ready.");
    }

    // ── Clone ────────────────────────────────────────────────────────────────

    private async Task CloneRvcAsync()
    {
        bool exists = Directory.Exists(rvcPath) && Directory.EnumerateFileSystemEntries(rvcPath).Any();

        if (!exists)
        {
            Log($"Cloning RVC from {RvcRepoUrl}...");
            string parent = Path.GetDirectoryName(rvcPath)!;
            string name   = Path.GetFileName(rvcPath);
            await RunAsync("git", $"clone \"{RvcRepoUrl}\" \"{name}\"", parent);
        }

        Log($"Checking out commit {RvcCommit[..7]}...");
        await RunAsync("git", $"checkout {RvcCommit}", rvcPath);
        Log("RVC ready.");
    }

    // ── Pip install ──────────────────────────────────────────────────────────

    private async Task InstallDependenciesAsync()
    {
        PatchRequirementsTxt();
        Log("Installing Python dependencies (first run only — this may take several minutes)...");
        await RunAsync(PythonPath, "-m pip install -r requirements.txt", rvcPath);
        // fairseq 0.12.2 on PyPI has an incomplete source tarball (missing
        // fairseq/clib/libbase/balanced_assignment.cpp) which causes the wheel build to
        // fail. Install from the git tag instead, which has all files.
        // --no-deps skips the omegaconf<2.1 dependency that modern pip cannot resolve.
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
        // HuBERT base model — required for voice conversion inference
        await DownloadIfMissingAsync(
            url:       "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/main/hubert_base.pt",
            localPath: Path.Combine(rvcPath, "assets", "hubert", "hubert_base.pt"),
            label:     "HuBERT base model");

        // rmvpe pitch model — required for the rmvpe pitch extraction method
        await DownloadIfMissingAsync(
            url:       "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/main/rmvpe.pt",
            localPath: Path.Combine(rvcPath, "assets", "rmvpe", "rmvpe.pt"),
            label:     "rmvpe pitch model");
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

    /// <summary>
    /// fairseq uses mutable class instances as dataclass field defaults in two patterns:
    ///   1.  name: Type = Type()
    ///   2.  name: Type = field(default=Type())
    /// Python 3.11 rejects both. This walks every .py file in the installed fairseq package
    /// and rewrites them to field(default_factory=Type), then patches __init__.py to
    /// swallow the hydra_init() failure caused by the incompatible omegaconf version.
    /// </summary>
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

        // Write a small helper script and run it so we avoid shell-escaping nightmares
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

        # Pattern 1: `name: Type = Type()`
        c, n1 = re.subn(
            r'(\s)(\w+): ([A-Z]\w*) = ([A-Z]\w*)\(\)',
            r'\1\2: \3 = field(default_factory=\4)', c)

        # Pattern 2: `name: Type = field(default=Type())`
        c, n2 = re.subn(
            r'(\s)(\w+): ([A-Z]\w*) = field\(default=([A-Z]\w*)\(\)\)',
            r'\1\2: \3 = field(default_factory=\4)', c)

        if c != orig:
            # Ensure `field` is imported
            if 'from dataclasses import' in c:
                c = re.sub(
                    r'(from dataclasses import )([^\n]+)',
                    lambda m: m.group(0) if 'field' in m.group(2)
                              else m.group(1) + m.group(2) + ', field',
                    c, count=1)
            open(path, 'w').write(c)
            fixed_files += 1

# Also patch __init__.py so hydra_init() failure (omegaconf version mismatch) is silent
init_path = os.path.join(fairseq_dir, '__init__.py')
if os.path.exists(init_path):
    c = open(init_path).read()
    if 'hydra_init()' in c and 'try:\n    hydra_init()' not in c:
        c = c.replace('hydra_init()',
            'try:\n    hydra_init()\nexcept Exception:\n    pass  # omegaconf version mismatch')
        open(init_path, 'w').write(c)
        fixed_files += 1

# Patch checkpoint_utils.py: PyTorch 2.6 changed torch.load default to weights_only=True,
# which breaks loading of legacy fairseq checkpoints that contain arbitrary Python objects.
# The two load sites need weights_only=False added as a top-level argument.
ckpt_path = os.path.join(fairseq_dir, 'checkpoint_utils.py')
if os.path.exists(ckpt_path):
    c = open(ckpt_path).read()
    orig_ckpt = c
    # Site 1: torch.load(f, map_location=torch.device("cpu"))
    c = c.replace(
        'torch.load(f, map_location=torch.device("cpu"))',
        'torch.load(f, map_location=torch.device("cpu"), weights_only=False)'
    )
    # Site 2: multi-line torch.load with lambda map_location
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
        // RunAsync is fire-and-forget-checked; use it synchronously via GetAwaiter
        RunAsync(PythonPath, $"\"{scriptPath}\" \"{fairseqDir}\"", rvcPath).GetAwaiter().GetResult();
        File.Delete(scriptPath);
    }

    /// <summary>
    /// The upstream requirements.txt contains packages that cannot be built on macOS or on
    /// Python 3.11+. Patch the file in-place right after checkout so pip never sees them.
    /// This is idempotent — re-running when already patched is a no-op.
    /// </summary>
    private void PatchRequirementsTxt()
    {
        string path = Path.Combine(rvcPath, "requirements.txt");
        if (!File.Exists(path)) return;

        var lines = File.ReadAllLines(path).ToList();
        bool changed = false;

        // aria2 — macOS is not a supported build platform for this package
        changed |= lines.RemoveAll(l => l.Trim().Equals("aria2", StringComparison.OrdinalIgnoreCase)) > 0;

        // fairseq — installed separately with --no-deps to bypass broken omegaconf metadata
        changed |= lines.RemoveAll(l => l.TrimStart().StartsWith("fairseq", StringComparison.OrdinalIgnoreCase)) > 0;

        // numba / llvmlite strict pins only support Python <3.11; relax to minimum compatible versions
        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith("numba==", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = "numba>=0.57.0";
                changed = true;
            }
            else if (trimmed.StartsWith("llvmlite==", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = "llvmlite>=0.40.0";
                changed = true;
            }
            else if (trimmed.StartsWith("numpy==", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = "numpy>=1.23.5";
                changed = true;
            }
            else if (trimmed.StartsWith("librosa==", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = "librosa>=0.9.1";
                changed = true;
            }
        }

        if (changed)
        {
            File.WriteAllLines(path, lines);
            Log("requirements.txt patched for macOS / Python 3.11 compatibility.");
        }
    }

    // ── .env ─────────────────────────────────────────────────────────────────

    private void WriteEnvFile()
    {
        // Use absolute paths for weight_root and index_root so voice files
        // can live outside the rvc directory tree.
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

    // ── Source patches ────────────────────────────────────────────────────────

    private void ApplyPatches()
    {
        PatchConfigPy();
        PatchInferWeb();
        PatchGradio();
    }

    private void PatchConfigPy()
    {
        string path = Path.Combine(rvcPath, "configs", "config.py");
        if (!File.Exists(path)) return;

        string content = File.ReadAllText(path);
        if (content.Contains("RVC_FORCE_CPU")) return; // already patched

        // Insert env-var check as first guard in has_mps()
        content = content.Replace(
            "    def has_mps() -> bool:\n        if not torch.backends.mps.is_available():",
            "    def has_mps() -> bool:\n" +
            "        if os.environ.get(\"RVC_FORCE_CPU\", \"0\") == \"1\":\n" +
            "            return False\n" +
            "        if not torch.backends.mps.is_available():"
        );

        File.WriteAllText(path, content);
    }

    private void PatchInferWeb()
    {
        string path = Path.Combine(rvcPath, "infer-web.py");
        if (!File.Exists(path)) return;

        string content = File.ReadAllText(path);

        // Patch 1: preload HuBERT at startup to avoid Gradio's 5 s internal timeout
        if (!content.Contains("_load_hubert"))
        {
            content = content.Replace(
                "config = Config()\nvc = VC(config)\n\n\nif config.dml == True:",
                "config = Config()\n" +
                "vc = VC(config)\n\n" +
                "# Preload HuBERT at startup so first inference doesn't breach Gradio's internal queue timeout\n" +
                "from infer.modules.vc.utils import load_hubert as _load_hubert\n" +
                "vc.hubert_model = _load_hubert(config)\n\n" +
                "if config.dml == True:"
            );
        }

        // Patch 2: increase Gradio queue/launch settings
        if (!content.Contains("status_update_rate=1"))
        {
            content = content.Replace(
                "app.queue(concurrency_count=511, max_size=1022).launch(\n" +
                "            server_name=\"0.0.0.0\",\n" +
                "            inbrowser=not config.noautoopen,\n" +
                "            server_port=config.listen_port,\n" +
                "            quiet=True,\n" +
                "        )",
                "app.queue(concurrency_count=511, max_size=1022, api_open=True, status_update_rate=1).launch(\n" +
                "            server_name=\"0.0.0.0\",\n" +
                "            inbrowser=not config.noautoopen,\n" +
                "            server_port=config.listen_port,\n" +
                "            quiet=True,\n" +
                "            max_threads=40,\n" +
                "        )"
            );
        }

        File.WriteAllText(path, content);
    }

    // ── Gradio package patches ────────────────────────────────────────────────

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

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
        Log("Installing Python dependencies (first run only — this may take several minutes)...");
        await RunAsync(PythonPath, "-m pip install -r requirements.txt", rvcPath);
        Log("Pinning faiss-cpu to compatible version...");
        await RunAsync(PythonPath, "-m pip install \"faiss-cpu==1.8.0.post1\" --force-reinstall", rvcPath);
        Log("Dependencies installed.");
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

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

public class StyleTtsSetupService(string styleTtsPath, ILogger? logger = null)
{
    private const string VENV_SUBDIR = "venv";
    private static string Python     => OperatingSystem.IsWindows() ? "python" : "/opt/homebrew/bin/python3.11";
    private static string PythonArgs => "";
    private const string REPO_URL    = "https://github.com/yl4579/StyleTTS2.git";

    public async Task Install()
    {
        string venv    = Path.Combine(styleTtsPath, VENV_SUBDIR);
        string repoDir = styleTtsPath;

        if (!Directory.Exists(styleTtsPath))
            Directory.CreateDirectory(styleTtsPath);

        // Clone the repo if not already present.
        // Clone into a temp dir first so we don't clobber an existing venv in repoDir.
        if (!File.Exists(Path.Combine(repoDir, "train_finetune.py")))
        {
            logger?.LogInformation("Cloning StyleTTS2 repository...");
            string tmp = repoDir + "_clone_tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
            await RunExe("git", $"clone {REPO_URL} \"{tmp}\"", Path.GetDirectoryName(styleTtsPath)!);

            foreach (string src in Directory.GetFiles(tmp))
                File.Copy(src, Path.Combine(repoDir, Path.GetFileName(src)), overwrite: true);
            foreach (string srcDir in Directory.GetDirectories(tmp))
            {
                string dirName = Path.GetFileName(srcDir);
                if (dirName == VENV_SUBDIR) continue;
                string dest = Path.Combine(repoDir, dirName);
                if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                Directory.Move(srcDir, dest);
            }
            Directory.Delete(tmp, recursive: true);
        }

        // train_finetune.py hardcodes device = 'cuda' — patch it to read from config
        string trainScript = Path.Combine(repoDir, "train_finetune.py");
        if (File.Exists(trainScript))
        {
            string src = File.ReadAllText(trainScript);
            string patched = src
                .Replace("device = 'cuda'", "device = config.get('device', 'cuda')")
                // forking after MPS init causes segfaults in DataLoader workers on macOS
                .Replace("num_workers=2,", "num_workers=0,")
                // MPS doesn't support float64; numpy defaults to float64 so cast explicitly
                .Replace("torch.from_numpy(y).to(device)", "torch.from_numpy(y).float().to(device)")
                // NaN loss triggers an interactive debugger which blocks the non-interactive process; skip instead
                .Replace(
                    "from IPython.core.debugger import set_trace\n                set_trace()",
                    "continue")
                // validation loop may have 0 iters if all batches are skipped; guard division
                .Replace(
                    "logger.info('Validation loss: %.3f, Dur loss: %.3f, F0 loss: %.3f' % (loss_test / iters_test, loss_align / iters_test, loss_f / iters_test) + '\\n\\n\\n')\n        print('\\n\\n\\n')\n        writer.add_scalar('eval/mel_loss', loss_test / iters_test, epoch + 1)\n        writer.add_scalar('eval/dur_loss', loss_test / iters_test, epoch + 1)\n        writer.add_scalar('eval/F0_loss', loss_f / iters_test, epoch + 1)",
                    "if iters_test > 0:\n            logger.info('Validation loss: %.3f, Dur loss: %.3f, F0 loss: %.3f' % (loss_test / iters_test, loss_align / iters_test, loss_f / iters_test) + '\\n\\n\\n')\n            writer.add_scalar('eval/mel_loss', loss_test / iters_test, epoch + 1)\n            writer.add_scalar('eval/dur_loss', loss_test / iters_test, epoch + 1)\n            writer.add_scalar('eval/F0_loss', loss_f / iters_test, epoch + 1)\n        print('\\n\\n\\n')");
            if (patched != src)
                File.WriteAllText(trainScript, patched);
        }

        if (!Directory.Exists(venv))
        {
            logger?.LogInformation("Creating StyleTTS2 virtual environment...");
            await RunExe(Python, $"{PythonArgs}-m venv \"{venv}\"", repoDir);
        }

        string pip    = Path.Combine(venv, OperatingSystem.IsWindows() ? @"Scripts\pip.exe"    : "bin/pip");
        string venvPy = Path.Combine(venv, OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python");
        logger?.LogInformation("Installing StyleTTS2 dependencies...");
        await RunExe(venvPy, "-m pip install -q --upgrade pip", repoDir);

        // Platform-specific torch: macOS gets default PyPI build (includes MPS),
        // Windows gets CUDA 12.1 build, Linux falls back to CPU.
        string torchInstall = OperatingSystem.IsMacOS()
            ? "install -q torch torchaudio"
            : OperatingSystem.IsWindows()
                ? "install -q torch torchaudio --index-url https://download.pytorch.org/whl/cu124"
                : "install -q torch torchaudio --index-url https://download.pytorch.org/whl/cpu";
        await RunExe(pip, torchInstall, repoDir);

        await RunExe(pip, $"install -q --prefer-binary -r \"{Path.Combine(repoDir, "requirements.txt")}\"", repoDir);
        await RunExe(pip, $"install -q openai-whisper flask sounddevice soundfile cached-path tensorboard pandas ipython gruut", repoDir);

        logger?.LogInformation("StyleTTS2 environment ready.");
    }

    private async Task RunExe(string exe, string args, string workDir)
    {
        ProcessStartInfo info = new()
        {
            FileName               = exe,
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = workDir,
        };

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException($"Failed to start: {exe}");

        Task stdoutTask = StreamLines(process.StandardOutput, line =>
        {
            if (!string.IsNullOrWhiteSpace(line))
                logger?.LogDebug("[setup] {Line}", line);
        });

        Task stderrTask = StreamLines(process.StandardError, line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
                logger?.LogDebug("[setup] {Line}", line);
            else
                logger?.LogWarning("[setup] {Line}", line);
        });

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new Exception($"Setup step failed (exit {process.ExitCode}): {exe} {args}");
    }

    private static async Task StreamLines(StreamReader reader, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            onLine(line);
    }
}

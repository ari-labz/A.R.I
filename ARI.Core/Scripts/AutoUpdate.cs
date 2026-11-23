using ARI.API;
using ARI.Common;
using ARI.LLM;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace ARI.Core;

/// <summary>
/// Updates ARI without anyone opening the Installer. Once an hour, when "Update automatically" is on, a newer stable
/// release exists, this isn't a dev run and the Installer started this server, it waits until nothing has been generating
/// for ten minutes, starts the Installer headless and shuts down with <see cref="ExitCode"/>. The Installer downloads the
/// release, waits for this process and the Console to exit, switches over and starts the new version.
/// See Documentation/Installer/Auto-Update.md.
/// </summary>
internal static class AutoUpdate
{
    public const int ExitCode = 75;   // the Console closes itself on this code so the new version's Console can open

    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan IdleFor    = TimeSpan.FromMinutes(10);

    public static void Start(Func<LLMModule?> llm, IHostApplicationLifetime lifetime, ILogger log, CancellationToken stop)
    {
        AutoUpdateStore.State state = AutoUpdateStore.Get();
        if (state.PendingFrom is { } from)
        {
            string result = from == BuildInfo.Version ? $"Update from {from} didn't take; still on {from}" : $"Updated {from} → {BuildInfo.Version}";
            log.LogInformation("[AutoUpdate] {Result}.", result);
            AutoUpdateStore.Update(s => { s.PendingFrom = null; s.LastResult = result; s.LastAt = DateTime.UtcNow; });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stop);
                while (!stop.IsCancellationRequested)
                {
                    try { if (await TryUpdate(llm, lifetime, log, stop)) return; }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("[AutoUpdate] Check failed: {Err}", ex.Message); }
                    await Task.Delay(CheckEvery, stop);
                }
            }
            catch (OperationCanceledException) { }
        }, stop);
    }

    private static async Task<bool> TryUpdate(Func<LLMModule?> llm, IHostApplicationLifetime lifetime, ILogger log, CancellationToken stop)
    {
        if (!AutoUpdateStore.Get().Enabled || Shared.DevMode) return false;
        string? installRoot = Environment.GetEnvironmentVariable("APP_INSTALL_ROOT");
        if (installRoot is null) return false;   // run from source, not an install the Installer manages

        await UpdateCheck.EnsureCheckedAsync();
        if (!UpdateCheck.Outdated) return false;

        // {base}/server/{version} → {base}/installer-path.txt, written by the Installer each time it opens.
        string? baseDir = Directory.GetParent(Path.GetFullPath(installRoot))?.Parent?.FullName;
        string pathFile = baseDir is null ? "" : Path.Combine(baseDir, "installer-path.txt");
        string? installer = File.Exists(pathFile) ? File.ReadAllText(pathFile).Trim() : null;
        if (installer is null || !File.Exists(installer))
        {
            Record($"{UpdateCheck.LatestVersion} is out, but the Installer couldn't be found. Open the Installer once so ARI knows where it is");
            return false;
        }

        if (!await WaitForIdle(llm, stop)) return false;

        List<string> args = ["--auto-update", "server", "--after-pid", Environment.ProcessId.ToString()];
        if (ParentProcessId() is int console) args.AddRange(["--after-pid", console.ToString()]);
        ProcessStartInfo psi = new(installer) { UseShellExecute = false };
        args.ForEach(psi.ArgumentList.Add);
        Process.Start(psi);

        log.LogInformation("[AutoUpdate] Idle and {Latest} is out: handing over to the Installer and shutting down.", UpdateCheck.LatestVersion);
        AutoUpdateStore.Update(s =>
        {
            s.PendingFrom = BuildInfo.Version;
            s.LastResult  = $"Handed {BuildInfo.Version} → {UpdateCheck.LatestVersion} to the Installer";
            s.LastAt      = DateTime.UtcNow;
        });
        Environment.ExitCode = ExitCode;
        lifetime.StopApplication();
        return true;

        void Record(string result)
        {
            log.LogInformation("[AutoUpdate] {Result}.", result);
            AutoUpdateStore.Update(s => { s.LastResult = result; s.LastAt = DateTime.UtcNow; });
        }
    }

    /// <summary>True once nothing has generated for <see cref="IdleFor"/>; false if that hasn't happened within the hour.</summary>
    private static async Task<bool> WaitForIdle(Func<LLMModule?> llm, CancellationToken stop)
    {
        DateTime start = DateTime.UtcNow, lastBusy = DateTime.UtcNow;
        while (DateTime.UtcNow - start < CheckEvery)
        {
            if (llm() is { IsIdle: false }) lastBusy = DateTime.UtcNow;
            else if (DateTime.UtcNow - lastBusy >= IdleFor) return true;
            await Task.Delay(TimeSpan.FromSeconds(5), stop);
        }
        return false;
    }

    // The Console that started this server, so the Installer waits for it to close too. Windows has no cheap way to ask; the
    // Console closes itself on ExitCode either way.
    private static int? ParentProcessId()
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            using Process ps = Process.Start(new ProcessStartInfo("ps", $"-o ppid= -p {Environment.ProcessId}") { RedirectStandardOutput = true })!;
            string text = ps.StandardOutput.ReadToEnd().Trim();
            ps.WaitForExit(2000);
            return int.TryParse(text, out int ppid) && ppid > 1 ? ppid : null;
        }
        catch { return null; }
    }
}

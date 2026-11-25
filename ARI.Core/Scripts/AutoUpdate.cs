using ARI.API;
using ARI.Common;
using ARI.LLM;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Compression;

namespace ARI.Core;

/// <summary>
/// Updates ARI on her own, with no Installer and no one at the machine. Five minutes after start and then each hour (or at once
/// after "Check now" in the control panel), when
/// "Update automatically" is on, this isn't a dev run and the server runs from an install ({base}/server/{version}), it
/// looks for a newer release (stable, or a 0.x pre-release). It downloads and unpacks it next to the running version, then
/// waits until nothing has been generating and nothing is waiting to be saved to the brain for ten minutes (dreams don't
/// count). It then points current.txt at the new version, starts a small relaunch script and stops with
/// <see cref="ExitCode"/>, which closes the Console. The script waits for both to exit, reopens the app (which starts the
/// version current.txt names), and if the new version doesn't answer within three minutes puts the old one back and
/// starts that instead. See Documentation/Installer/Auto-Update.md.
/// </summary>
internal static class AutoUpdate
{
    public const int ExitCode = 75;   // the Console closes itself on this code so the relaunched app can open

    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan IdleFor    = TimeSpan.FromMinutes(10);
    private const int KEEP_VERSIONS = 3;    // old versions kept for a quick manual rollback

    private static readonly HttpClient http = CreateClient();

    public static void Start(Func<LLMModule?> llm, IHostApplicationLifetime lifetime, int apiPort, ILogger log, CancellationToken stop)
    {
        AutoUpdateStore.State state = AutoUpdateStore.Get();
        if (state.PendingFrom is { } from)
        {
            bool took = from != BuildInfo.Version;
            string result = took ? $"Updated {from} → {BuildInfo.Version}" : $"Update from {from} didn't take; still on {from}";
            log.LogInformation("[AutoUpdate] {Result}.", result);
            AutoUpdateStore.Update(s => { s.PendingFrom = null; s.LastResult = result; s.LastAt = DateTime.UtcNow; });
            if (took) RemoveOldVersions(log);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await AutoUpdateStore.WaitForNextCheck(TimeSpan.FromMinutes(5), stop);
                while (!stop.IsCancellationRequested)
                {
                    AutoUpdateStore.Busy = true;
                    try { if (await TryUpdate(llm, lifetime, apiPort, log, stop)) return; }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning("[AutoUpdate] {Err}", ex.Message);
                        AutoUpdateStore.Update(s => { s.LastResult = $"Update failed: {ex.Message}"; s.LastAt = DateTime.UtcNow; });
                    }
                    finally { AutoUpdateStore.Busy = false; }
                    await AutoUpdateStore.WaitForNextCheck(CheckEvery, stop);   // "Check now" in the control panel cuts this short
                }
            }
            catch (OperationCanceledException) { }
        }, stop);
    }

    /// <summary>The folder that holds one folder per installed version plus current.txt, or null when not run from an install.</summary>
    private static string? ServerDir()
    {
        string? installRoot = Environment.GetEnvironmentVariable("APP_INSTALL_ROOT");
        return installRoot is null ? null : Directory.GetParent(Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar))?.FullName;
    }

    private static async Task<bool> TryUpdate(Func<LLMModule?> llm, IHostApplicationLifetime lifetime, int apiPort, ILogger log, CancellationToken stop)
    {
        if (!AutoUpdateStore.Get().Enabled || Shared.DevMode) return false;
        if (ServerDir() is not { } serverDir) return false;   // run from source, not an install

        await UpdateCheck.EnsureCheckedAsync(force: true);
        if (!UpdateCheck.Outdated || UpdateCheck.LatestVersion is not { } version) return false;

        string asset = $"ARI_Server_v{version}_{Platform()}.zip";
        string url = UpdateCheck.AssetUrl(asset) ?? throw new InvalidOperationException($"{version} is out but has no {asset} to download.");

        string versionDir = await Unpack(serverDir, version, url, log, stop);

        if (!await WaitForQuiet(llm, stop)) { Record(log, $"{version} is ready; waiting for a quiet moment to switch over"); return false; }

        string current  = BuildInfo.Version;
        string pidsArg  = string.Join(" ", new[] { Environment.ProcessId }.Concat(ParentProcessId() is int c ? [c] : Array.Empty<int>()));
        await File.WriteAllTextAsync(Path.Combine(serverDir, "current.txt"), version, stop);
        StartRelaunch(serverDir, current, version, apiPort, pidsArg);

        log.LogInformation("[AutoUpdate] Quiet and {Version} is ready: switching over and restarting.", version);
        AutoUpdateStore.Update(s =>
        {
            s.PendingFrom = current;
            s.LastResult  = $"Restarting {current} → {version}";
            s.LastAt      = DateTime.UtcNow;
        });
        Environment.ExitCode = ExitCode;
        lifetime.StopApplication();
        return true;
    }

    /// <summary>Downloads and unpacks a version into serverDir/version, unless it's already there. Unpacks into a staging
    /// folder first, so a half-finished download is never mistaken for an install.</summary>
    private static async Task<string> Unpack(string serverDir, string version, string url, ILogger log, CancellationToken stop)
    {
        string target = Path.Combine(serverDir, version);
        if (File.Exists(Path.Combine(target, OperatingSystem.IsWindows() ? "ARI.Core.exe" : "ARI.Core"))) return target;

        string staging = Path.Combine(serverDir, $".update-{version}");
        string zip     = staging + ".zip";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        log.LogInformation("[AutoUpdate] Downloading {Version}: {Url}", version, url);
        try
        {
            await using (Stream s = await http.GetStreamAsync(url, stop))
            await using (FileStream fs = File.Create(zip))
                await s.CopyToAsync(fs, stop);

            if (OperatingSystem.IsWindows()) ZipFile.ExtractToDirectory(zip, staging, overwriteFiles: true);
            else
            {
                // unzip keeps the symlinks inside the Mac app bundle; .NET's extractor doesn't.
                (int code, string output) = await Run("unzip", ["-oq", zip, "-d", staging], stop);
                if (code != 0) throw new InvalidOperationException($"Couldn't unpack {version}: {output.Trim()}");
                foreach (string f in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                    if (Path.GetFileName(f) == "ARI.Core" || (Path.GetExtension(f).Length == 0 && Path.GetDirectoryName(f) == Path.Combine(staging, "Console")))
                        File.SetUnixFileMode(f, File.GetUnixFileMode(f) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            if (!File.Exists(Path.Combine(staging, OperatingSystem.IsWindows() ? "ARI.Core.exe" : "ARI.Core")))
                throw new InvalidOperationException($"The {version} download has no server in it.");

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
            log.LogInformation("[AutoUpdate] {Version} unpacked to {Dir}.", version, target);
            return target;
        }
        finally
        {
            try { File.Delete(zip); } catch { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    /// <summary>True once nothing has generated and nothing has waited to be saved to the brain for <see cref="IdleFor"/>;
    /// false if that hasn't happened within the hour. Dreams don't count as busy.</summary>
    private static async Task<bool> WaitForQuiet(Func<LLMModule?> llm, CancellationToken stop)
    {
        DateTime start = DateTime.UtcNow, lastBusy = DateTime.UtcNow;
        while (DateTime.UtcNow - start < CheckEvery)
        {
            if (llm() is { } m && (!m.IsIdle || !m.MemoryQueueEmpty)) lastBusy = DateTime.UtcNow;
            else if (DateTime.UtcNow - lastBusy >= IdleFor) return true;
            await Task.Delay(TimeSpan.FromSeconds(5), stop);
        }
        return false;
    }

    // ── Relaunch ─────────────────────────────────────────────────────────────

    /// <summary>Starts the script that waits for this server and its Console to exit, opens the app again on the new version
    /// and checks it answers; it outlives this process. Its log is {base}/autoupdate.log.</summary>
    private static void StartRelaunch(string serverDir, string from, string to, int port, string pids)
    {
        string baseDir = Directory.GetParent(serverDir)!.FullName;
        string script  = Path.Combine(serverDir, OperatingSystem.IsWindows() ? "relaunch.ps1" : "relaunch.sh");
        File.WriteAllText(script, OperatingSystem.IsWindows() ? WindowsScript : UnixScript);

        // On Unix it runs in the background under nohup with its output thrown away, so it neither holds the Console's pipe
        // open nor dies when the Console closes.
        ProcessStartInfo psi = OperatingSystem.IsWindows()
            ? new("powershell.exe") { ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script } }
            : new("/bin/bash") { ArgumentList = { "-c", "nohup /bin/bash \"$0\" \"$@\" >/dev/null 2>&1 &", script } };
        foreach (string a in new[] { serverDir, baseDir, from, to, port.ToString(), pids }) psi.ArgumentList.Add(a);
        psi.UseShellExecute = false;
        Process.Start(psi)?.WaitForExit(5000);
    }

    // Args: serverDir baseDir fromVersion toVersion port "pid pid". Waits for the pids, opens the app, rolls back if needed.
    private const string UnixScript = """
        #!/bin/bash
        SERVER="$1"; BASE="$2"; FROM="$3"; TO="$4"; PORT="$5"; PIDS="$6"
        LOG="$BASE/autoupdate.log"
        log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*" >> "$LOG"; }
        trap '' HUP
        log "switching $FROM -> $TO"
        for p in $PIDS; do for i in $(seq 1 120); do kill -0 "$p" 2>/dev/null || break; sleep 1; done; done

        MACAPP=""
        for a in "/Applications/A·R·I.app" "/Applications/A.R.I.app"; do [ -d "$a" ] && MACAPP="$a" && break; done
        copy_app() {   # keep the /Applications copy in step with the version it starts
            [ -n "$MACAPP" ] && [ -d "$SERVER/$1/A.R.I.app" ] || return 0
            rm -rf "$MACAPP.updating" && cp -R "$SERVER/$1/A.R.I.app" "$MACAPP.updating" && rm -rf "$MACAPP" && mv "$MACAPP.updating" "$MACAPP" \
                || { rm -rf "$MACAPP.updating"; log "couldn't replace $MACAPP (it still starts the version in current.txt)"; }
        }
        launch() {
            if [ "$(uname)" = "Darwin" ]; then
                if [ -n "$MACAPP" ]; then open "$MACAPP"; else open "$SERVER/$1/A.R.I.app"; fi
            else
                C=$(find "$SERVER/$1/Console" -maxdepth 1 -type f ! -name "*.*" ! -name "ARI.Core" | head -1)
                if [ -n "$C" ]; then nohup "$C" >/dev/null 2>&1 & else (cd "$SERVER/$1" && APP_INSTALL_ROOT="$SERVER/$1" nohup ./ARI.Core >/dev/null 2>&1 &); fi
            fi
        }
        up() { for i in $(seq 1 90); do curl -s -o /dev/null -m 2 "http://127.0.0.1:$PORT/" && return 0; sleep 2; done; return 1; }

        copy_app "$TO"; launch "$TO"
        if up; then log "$TO is up"; exit 0; fi

        log "$TO didn't answer within 3 minutes; going back to $FROM"
        pkill -f "$SERVER/$TO/" 2>/dev/null; sleep 3
        echo "$FROM" > "$SERVER/current.txt"
        copy_app "$FROM"; launch "$FROM"
        if up; then log "$FROM is back up"; else log "$FROM didn't answer either"; fi
        """;

    private const string WindowsScript = """
        param($Server, $Base, $From, $To, $Port, $Pids)
        $log = Join-Path $Base "autoupdate.log"
        function Log($m) { Add-Content $log "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $m" }
        Log "switching $From -> $To"
        foreach ($p in ($Pids -split ' ')) { if ($p) { Wait-Process -Id $p -Timeout 120 -ErrorAction SilentlyContinue } }
        function Launch($v) {
            $cmd = Join-Path $Base "ari-launch.cmd"
            if (Test-Path $cmd) { Start-Process $cmd -WindowStyle Hidden; return }
            $console = Get-ChildItem (Join-Path $Server "$v\Console") -Filter *.exe -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike "ARI.Core*" } | Select-Object -First 1
            if ($console) { Start-Process $console.FullName } else { Start-Process (Join-Path $Server "$v\ARI.Core.exe") -WorkingDirectory (Join-Path $Server $v) }
        }
        function Up { for ($i = 0; $i -lt 90; $i++) { try { Invoke-WebRequest "http://127.0.0.1:$Port/" -UseBasicParsing -TimeoutSec 2 | Out-Null; return $true } catch { Start-Sleep 2 } }; return $false }
        Launch $To
        if (Up) { Log "$To is up"; exit 0 }
        Log "$To didn't answer within 3 minutes; going back to $From"
        Get-Process | Where-Object { $_.Path -like (Join-Path $Server "$To\*") } | Stop-Process -Force -ErrorAction SilentlyContinue
        Set-Content (Join-Path $Server "current.txt") $From
        Launch $From
        if (Up) { Log "$From is back up" } else { Log "$From didn't answer either" }
        """;

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Keeps the running version and the newest few others; the rest are deleted.</summary>
    private static void RemoveOldVersions(ILogger log)
    {
        if (ServerDir() is not { } serverDir) return;
        var old = Directory.EnumerateDirectories(serverDir)
            .Select(d => Path.GetFileName(d))
            .Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d+\.\d+\.\d+") && n != BuildInfo.Version)
            .OrderByDescending(n => n, Comparer<string>.Create(UpdateCheck.CompareVersions))
            .Skip(KEEP_VERSIONS - 1);
        foreach (string v in old)
            try { Directory.Delete(Path.Combine(serverDir, v), recursive: true); log.LogInformation("[AutoUpdate] Removed old version {Version}.", v); }
            catch (Exception ex) { log.LogWarning("[AutoUpdate] Couldn't remove {Version}: {Err}", v, ex.Message); }
    }

    private static string Platform() =>
        OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "mac" : "linux";

    private static void Record(ILogger log, string result)
    {
        log.LogInformation("[AutoUpdate] {Result}.", result);
        AutoUpdateStore.Update(s => { s.LastResult = result; s.LastAt = DateTime.UtcNow; });
    }

    private static async Task<(int Code, string Output)> Run(string exe, string[] args, CancellationToken ct)
    {
        ProcessStartInfo psi = new(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {exe}.");
        Task<string> o = p.StandardOutput.ReadToEndAsync(ct), e = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await o + await e);
    }

    private static HttpClient CreateClient()
    {
        HttpClient c = new() { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ARI-Server/1.0");
        return c;
    }

    // The Console that started this server, so the relaunch waits for it to close too. Windows has no cheap way to ask; the
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

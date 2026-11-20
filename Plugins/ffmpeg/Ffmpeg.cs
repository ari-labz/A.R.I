[Plugin("ffmpeg", "Lets Ari look inside video: facts about a clip, and still frames she can open and see.")]
public class FfmpegPlugin : ScriptPlugin
{
    public override IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new("ffmpeg_path", "ffmpeg program", PluginSettingType.Text, "The ffmpeg program to run. Leave as ffmpeg if it is installed normally; ffprobe is expected beside it.", "ffmpeg"),
    ];

    protected override Task OnStart()
    {
        StatusText = FfmpegRun.Version(Text("ffmpeg_path")) is { } v ? "Ready: " + v : "ffmpeg not found. Install it or set its path.";
        return Task.CompletedTask;
    }
}

/// <summary>Runs ffmpeg or ffprobe and hands back what it printed.</summary>
public static class FfmpegRun
{
    public static string Program(string ffmpegPath, bool probe)
    {
        string p = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();
        return probe ? (p.EndsWith("ffmpeg", StringComparison.OrdinalIgnoreCase) ? p[..^6] + "ffprobe" : "ffprobe") : p;
    }

    public static string? Version(string ffmpegPath)
    {
        try { return Run(Program(ffmpegPath, false), new[] { "-version" }, 8).Split('\n')[0].Trim(); } catch { return null; }
    }

    public static string Run(string program, IEnumerable<string> args, int timeoutSeconds)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi) ?? throw new Exception("Could not start " + program);
        var err = proc.StandardError.ReadToEndAsync();
        string output = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(timeoutSeconds * 1000)) { try { proc.Kill(true); } catch { } throw new Exception("Timed out."); }
        return proc.ExitCode == 0 ? output : throw new Exception((err.Result.Length > 0 ? err.Result : output).Trim());
    }

    public static string Resolve(string path, string working)
    {
        string full = Path.IsPathRooted(path) ? path : Path.Combine(working, path);
        return File.Exists(full) ? full : throw new ToolArgException($"No file at {full}.");
    }
}

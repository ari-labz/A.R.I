[Tool("audio_look", "Look at a sound file (or a video's sound): draws its waveform and spectrogram as one picture you can open with read_file, and lists its loudness and every silence. Use it to compare takes, find gaps or clipping, or check a recording before using it.", Access = ToolAccess.ReadWrite)]
public class AudioLook : Tool
{
    [Param("Path to the audio or video file, relative to the working folder or absolute.")]
    public string Path;

    [Param("How quiet counts as silence, in dB (default -40).", Optional = true)]
    public int? SilenceDb;

    [Param("How long a quiet stretch must last to count as silence, in seconds (default 0.4).", Optional = true)]
    public double? MinSilence;

    public override Task<string> Run()
    {
        var plugin = ScriptPlugin.Get("ffmpeg");
        string ffmpeg = plugin.Text("ffmpeg_path");
        string file = FfmpegRun.Resolve(Path, WorkingFolder);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int db = SilenceDb ?? -40;
        double min = MinSilence ?? 0.4;

        string folder = System.IO.Path.Combine(WorkingFolder, "audio_looks");
        Directory.CreateDirectory(folder);
        string picture = System.IO.Path.Combine(folder, System.IO.Path.GetFileNameWithoutExtension(file) + "_look.png");
        // Waveform above, spectrogram below, in one image.
        FfmpegRun.Run(ffmpeg, new[] { "-y", "-v", "error", "-i", file, "-filter_complex",
            "[0:a]aformat=channel_layouts=mono,asplit=2[a][b];[a]showwavespic=s=1200x240:colors=0x5fb3c8[w];[b]showspectrumpic=s=1200x320:legend=0:color=magma:fscale=log[s];[w][s]vstack=inputs=2",
            "-frames:v", "1", picture }, 120);

        // silencedetect and the loudness summary are printed on stderr, which FfmpegRun.Run only returns on failure, so this reads stderr itself.
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string a in new[] { "-v", "info", "-nostats", "-i", file, "-af", $"silencedetect=noise={db}dB:d={min.ToString(inv)},ebur128", "-f", "null", "-" }) psi.ArgumentList.Add(a);
        string log;
        using (var proc = System.Diagnostics.Process.Start(psi))
        {
            _ = proc.StandardOutput.ReadToEndAsync();
            log = proc.StandardError.ReadToEnd();
            proc.WaitForExit(120000);
        }

        var lines = new List<string> { $"Picture: audio_looks/{System.IO.Path.GetFileName(picture)} (waveform on top, spectrogram below; open it with read_file)." };
        var dur = System.Text.RegularExpressions.Regex.Match(log, @"Duration: (\d+:\d+:\d+\.\d+)");
        if (dur.Success) lines.Add("Length: " + dur.Groups[1].Value);
        var integrated = System.Text.RegularExpressions.Regex.Matches(log, @"I:\s+(-?[\d.]+) LUFS");
        var range = System.Text.RegularExpressions.Regex.Matches(log, @"LRA:\s+([\d.]+) LU");
        if (integrated.Count > 0) lines.Add($"Loudness: {integrated[^1].Groups[1].Value} LUFS" + (range.Count > 0 ? $", range {range[^1].Groups[1].Value} LU" : ""));
        var starts = System.Text.RegularExpressions.Regex.Matches(log, @"silence_start: (-?[\d.]+)");
        var ends = System.Text.RegularExpressions.Regex.Matches(log, @"silence_end: ([\d.]+) \| silence_duration: ([\d.]+)");
        if (starts.Count == 0) lines.Add($"No silences (quieter than {db} dB for {min.ToString(inv)} s or more).");
        else
        {
            lines.Add($"Silences (quieter than {db} dB for {min.ToString(inv)} s or more):");
            for (int i = 0; i < starts.Count && i < 40; i++)
                lines.Add(i < ends.Count ? $"- {starts[i].Groups[1].Value}s to {ends[i].Groups[1].Value}s ({ends[i].Groups[2].Value}s)" : $"- from {starts[i].Groups[1].Value}s to the end");
        }
        return Task.FromResult(string.Join("\n", lines));
    }
}

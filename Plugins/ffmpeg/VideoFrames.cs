[Tool("video_frames", "Pull still frames out of a video so you can look at them. Give moments in seconds, or a count to spread evenly across the clip. Returns image files; open each with read_file to see it.", Access = ToolAccess.ReadWrite)]
public class VideoFrames : Tool
{
    [Param("Path to the video, relative to the working folder or absolute.")]
    public string Path;

    [Param("Moments to capture, in seconds, separated by commas, e.g. 0,12.5,40. Leave out to spread frames evenly.", Optional = true)]
    public string At;

    [Param("How many frames to spread evenly across the video when At is not given (1 to 12, default 6).", Optional = true)]
    public int? Count;

    [Param("Width of each frame in pixels (default 768). Smaller frames cost less to look at.", Optional = true)]
    public int? Width;

    public override Task<string> Run()
    {
        var plugin = ScriptPlugin.Get("ffmpeg");
        string ffmpeg = plugin.Text("ffmpeg_path");
        string file = FfmpegRun.Resolve(Path, WorkingFolder);
        int width = Math.Clamp(Width ?? 768, 128, 1920);

        var times = new List<double>();
        if (!string.IsNullOrWhiteSpace(At))
            foreach (string part in At.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                times.Add(double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v >= 0 ? v : throw new ToolArgException($"'{part}' is not a time in seconds."));
        else
        {
            string dur = FfmpegRun.Run(FfmpegRun.Program(ffmpeg, true), new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file }, 30).Trim();
            double length = double.TryParse(dur, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double l) ? l : 0;
            int n = Math.Clamp(Count ?? 6, 1, 12);
            for (int i = 0; i < n; i++) times.Add(Math.Round(length * (i + 0.5) / n, 2));
        }
        if (times.Count > 12) throw new ToolArgException("Ask for at most 12 frames at once.");

        string folder = System.IO.Path.Combine(WorkingFolder, "video_frames");
        Directory.CreateDirectory(folder);
        string stem = System.IO.Path.GetFileNameWithoutExtension(file);
        var made = new List<string>();
        foreach (double t in times)
        {
            string name = $"{stem}_{t.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}s.jpg";
            string output = System.IO.Path.Combine(folder, name);
            FfmpegRun.Run(ffmpeg, new[] { "-y", "-v", "error", "-ss", t.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", file, "-frames:v", "1", "-vf", $"scale={width}:-2", "-q:v", "3", output }, 60);
            if (File.Exists(output)) made.Add($"{t:0.##}s -> video_frames/{name}");
        }
        return Task.FromResult(made.Count == 0 ? "No frames could be read at those moments." : "Frames saved. Open each with read_file to see it:\n" + string.Join("\n", made));
    }
}

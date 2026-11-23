[Tool("video_info", "Facts about a video file: length, size in pixels, frame rate and whether it has sound. Use it before video_frames to pick moments worth looking at.", Access = ToolAccess.Read)]
public class VideoInfo : Tool
{
    [Param("Path to the video, relative to the working folder or absolute.")]
    public string Path;

    public override Task<string> Run()
    {
        var plugin = ScriptPlugin.Get("ffmpeg");
        string file = FfmpegRun.Resolve(Path, WorkingFolder);
        string json = FfmpegRun.Run(FfmpegRun.Program(plugin.Text("ffmpeg_path"), true),
            new[] { "-v", "error", "-show_entries", "format=duration,size:stream=codec_type,codec_name,width,height,r_frame_rate", "-of", "json", file }, 30);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        string duration = root.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d) ? d.GetString() : "?";
        var lines = new List<string> { $"File: {file}", $"Length: {duration} seconds" };
        if (root.TryGetProperty("streams", out var streams))
            foreach (var s in streams.EnumerateArray())
            {
                string type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : "?";
                if (type == "video") lines.Add($"Video: {s.GetProperty("width")}x{s.GetProperty("height")}, {s.GetProperty("r_frame_rate").GetString()} fps, {s.GetProperty("codec_name").GetString()}");
                else if (type == "audio") lines.Add($"Audio: {s.GetProperty("codec_name").GetString()}");
            }
        return Task.FromResult(string.Join("\n", lines));
    }
}

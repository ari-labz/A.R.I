using System.Text.Json;
using ARI.Common;

namespace ARI.LLM;

/// <summary>Lets Ari see an animation she made the way an animator checks one: frame by frame. The page (an .html file in the working folder,
/// or HTML given directly) runs on a clock that only moves when a frame is taken, so each frame is exactly that moment, and the frames come back
/// as one labelled contact sheet she can look at. Alongside them she gets the animation rulebook (AnimationRules.md in the data folder), which
/// starts with the classic principles and grows with what the owner teaches her through add_rule.</summary>
internal sealed class PreviewAnimation : Tool
{
    private readonly Thread thread;
    internal PreviewAnimation(Thread thread) => this.thread = thread;

    internal override string Name => "preview_animation";

    private static string RulebookPath => Path.Combine(Paths.PersistentData, "AnimationRules.md");

    private const string DefaultRules = """
        # Animation rulebook
        Check every animation against these before showing it. Lessons the owner taught are at the end; they win over the general rules.

        1. Timing: UI motion is 150–400 ms; anything longer needs a reason. Big things move slower than small things.
        2. Easing: nothing real starts or stops instantly. Ease out for things arriving, ease in for things leaving, ease in-out for things moving across.
        3. Anticipation: a big move reads better with a small wind-up the other way first.
        4. Follow-through: parts that trail (hair, cloth, a tail, a list's later items) settle a little after the main body stops.
        5. Squash and stretch: soft things keep their volume while they deform; hard things don't deform at all.
        6. Arcs: living things move along curves, not straight lines.
        7. Staging: one thing moves at a time where the eye should go; don't animate everything at once.
        8. Overlap: stagger related items by 20–60 ms rather than moving them together.
        9. No popping: check consecutive frames for anything that jumps, flickers, appears without a transition or is cut off by its container.
        10. Ending: the last frame must be a clean resting state, identical to the static design.

        ## Lessons from the owner
        """;

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "preview_animation",
            description = "See an animation frame by frame, like an animator scrubbing a timeline. Give an .html file in your working folder (or html directly) and either times (ms, e.g. \"0,100,200,400,800\") " +
                          "or duration plus frames to space them evenly. CSS animations and transitions, requestAnimationFrame, timers and libraries like GSAP all run on a clock that stops at each moment, " +
                          "so every frame is exact. You get one image with every frame labelled, plus the animation rulebook: check the frames against it and fix what breaks it before showing the person. " +
                          "When the person tells you something about how they like motion to look, save it with add_rule so you remember it next time.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    file     = new { type = "string", description = "An .html file in your working folder, e.g. one show_visual saved." },
                    html     = new { type = "string", description = "Or the page itself, if it isn't saved as a file." },
                    times    = new { type = "string", description = "Moments to capture in ms from page load, comma-separated. Up to 24." },
                    duration = new { type = "integer", description = "Instead of times: how long to watch, in ms (default 1000)." },
                    frames   = new { type = "integer", description = "Instead of times: how many evenly spaced frames, including the first and last (default 8, up to 24)." },
                    width    = new { type = "integer", description = "Page width in px (default 480)." },
                    height   = new { type = "integer", description = "Page height in px (default 360)." },
                    add_rule = new { type = "string", description = "Save a lesson about motion the person taught you to the rulebook, in one sentence. Nothing is rendered when only this is given." },
                },
            }
        }
    };

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        JsonElement root = doc.RootElement;
        string Str(string k) => root.TryGetProperty(k, out JsonElement v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
        int Int(string k, int fallback, int min, int max) => int.TryParse(Str(k), out int n) ? Math.Clamp(n, min, max) : fallback;

        string rules = Rulebook();
        if (Str("add_rule").Trim() is { Length: > 0 } lesson)
        {
            File.AppendAllText(RulebookPath, $"- {DateTime.Now:yyyy-MM-dd}: {lesson.ReplaceLineEndings(" ")}\n");
            if (Str("file").Length == 0 && Str("html").Length == 0) return (ToolResult)"Saved to the animation rulebook. It's checked every time you preview an animation.";
            rules = Rulebook();
        }

        string html = Str("html");
        if (html.Trim().Length == 0 && Str("file") is { Length: > 0 } file)
        {
            string folder = thread.FilesystemRoot is { Length: > 0 } r ? r : Paths.ScratchpadDir(thread.Key);
            string full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(folder, file));
            if (!File.Exists(full)) return (ToolResult)$"[Error: {file} isn't in your working folder.]";
            if (!full.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !full.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                return (ToolResult)"[Error: preview_animation plays .html pages. Put the animation in an HTML page first.]";
            html = File.ReadAllText(full);
        }
        if (html.Trim().Length < 10) return (ToolResult)"[Error: give file (an .html in your working folder) or html.]";

        List<int> times = Str("times").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s.TrimEnd('m', 's'), out int t) ? t : -1).Where(t => t >= 0).Distinct().Order().Take(24).ToList();
        if (times.Count == 0)
        {
            int duration = Int("duration", 1000, 1, 60_000), count = Int("frames", 8, 2, 24);
            times = Enumerable.Range(0, count).Select(i => (int)Math.Round(i * duration / (double)(count - 1))).Distinct().ToList();
        }

        try
        {
            (byte[] sheet, List<string> errors) = await BrowserHost.RenderFrames(html, times, Int("width", 480, 160, 1280), Int("height", 360, 120, 1000));
            string note = $"{times.Count} frames at {string.Join(", ", times)} ms." +
                          (errors.Count > 0 ? $" The page raised errors: {string.Join("; ", errors)}." : "") +
                          $"\n\nCheck them against the rulebook:\n{rules}";
            return ToolResult.AsImage(sheet, "image/png", note);
        }
        catch (Exception e)
        {
            return (ToolResult)$"[Error: couldn't render the frames: {e.Message.Split('\n')[0]}]";
        }
    }

    private static string Rulebook()
    {
        if (!File.Exists(RulebookPath)) { Directory.CreateDirectory(Paths.PersistentData); File.WriteAllText(RulebookPath, DefaultRules); }
        return File.ReadAllText(RulebookPath);
    }
}

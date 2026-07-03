using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>
/// The "Appraisal" agent — a cheap pre-pass that grades how much THINKING a prompt warrants, 0–10, BEFORE a
/// thinking-enabled turn runs. The grade maps to a wall-clock thinking-time budget (<see cref="GradeToSeconds"/>)
/// so the model gets only as much deliberation as the task needs and never over-thinks. Grade 0 = no thinking.
/// </summary>
internal class Appraisal : Agent
{
    public Appraisal() { }

    /// <summary>Grades the prompt 0–10. Defaults to 0 (no thinking) if the reply isn't a clean number.</summary>
    internal async Task<int> Appraise(string message, CancellationToken ct = default)
    {
        Thread ephemeral = new Thread(ThreadPipeline.Dialogue, $"__appraise_{Guid.NewGuid():N}") { Internal = true };
        string result    = await SendPrompt(ephemeral, message, ct: ct);
        return ParseGrade(result);
    }

    /// <summary>Extracts the first integer in the reply and clamps it to 0–10. Unparseable → 0 (no thinking).</summary>
    internal static int ParseGrade(string reply)
    {
        Match m = Regex.Match(reply ?? "", @"-?\d+");
        return m.Success && int.TryParse(m.Value, out int g) ? Math.Clamp(g, 0, 10) : 0;
    }

    /// <summary>
    /// Grade → wall-clock thinking-time budget in seconds. -1 = no limit (grade 10).
    /// 0:3s · 1:10s · 2:20s · 3:30s · 4:1m · 5:2m · 6:4m · 7:6m · 8:10m · 9:15m · 10:∞
    /// Grade 0 is a TINY budget, not think-off: thinking on/off is fixed at the pipeline level and never
    /// flipped per turn — a flip changes the chat template and invalidates the server's whole KV prefix.
    /// A 3s budget means the 100% finish-sentence cue fires almost immediately, so a trivial prompt still
    /// costs only a breath of reasoning while the cache stays warm.
    /// </summary>
    internal static int GradeToSeconds(int grade) => grade switch
    {
        <= 0 => 3,
        1    => 10,
        2    => 20,
        3    => 30,
        4    => 60,
        5    => 120,
        6    => 240,
        7    => 360,
        8    => 600,
        9    => 900,
        _    => -1,   // 10 (or higher) → unlimited
    };
}

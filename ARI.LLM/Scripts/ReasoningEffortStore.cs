using ARI.Common;

namespace ARI.LLM;

/// <summary>
/// The one global "how hard should the model think" dial. See Documentation/Server/ARI.LLM/Reasoning-Effort.
///
/// A single app-wide step (0/1/2 = low/medium/xhigh) that does two things at once:
///   • sends the model-side <c>reasoning_effort</c> steering signal (only to models that support it), and
///   • multiplies every agent's own <c>BudgetThinking</c> cap by the step's factor.
///
/// Low is the anchor (×1); medium and high grow the cap above it, they never shrink it. The step is
/// universal — there is no per-agent override — so an agent's relative tuning is preserved while the
/// dial scales all of them by the same factor. Persisted as last-used, seeded to Low.
/// </summary>
public static class ReasoningEffortStore
{
    /// <summary>Wire values as Qwen 3.8 exposes them — three honest levels, no invented middle stops.</summary>
    public static readonly string[] Levels = ["low", "medium", "xhigh"];

    /// <summary>Backstop multipliers applied to <c>BudgetThinking</c>. Hardcoded starting point; retune from
    /// the reasoning_effort tagged run logs so the cap only ever catches runaways.</summary>
    public static readonly double[] Multipliers = [1.0, 2.5, 5.0];

    private static readonly string FilePath = Path.Combine(Paths.PersistentData, "reasoning-effort.txt");
    private static readonly object Lock = new();

    /// <summary>Current step, clamped to a valid index. Defaults to 0 (low) on first run / unreadable file.</summary>
    public static int Step
    {
        get
        {
            lock (Lock)
            {
                try
                {
                    if (File.Exists(FilePath) && int.TryParse(File.ReadAllText(FilePath).Trim(), out int s))
                        return Math.Clamp(s, 0, Levels.Length - 1);
                }
                catch { /* fall through to default */ }
                return 0;
            }
        }
        set
        {
            lock (Lock)
            {
                int s = Math.Clamp(value, 0, Levels.Length - 1);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, s.ToString());
            }
        }
    }

    /// <summary>The <c>reasoning_effort</c> wire value for the current step.</summary>
    public static string Level => Levels[Step];

    /// <summary>The thinking-budget multiplier for the current step.</summary>
    public static double Multiplier => Multipliers[Step];
}

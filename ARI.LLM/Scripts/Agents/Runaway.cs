namespace ARI.LLM;

internal static class Runaway
{
    private const int    MIN_CHARS    = 2000;
    private const int    SAMPLE_CHARS = 600;
    private const double DOMINANCE    = 0.6;

    internal static bool IsSpiral(System.Text.StringBuilder sb, out char domChar, out double ratio)
    {
        domChar = '\0';
        ratio   = 0;
        if (sb.Length <= MIN_CHARS) return false;
        string recent = sb.ToString();
        recent = recent.Length > SAMPLE_CHARS ? recent[^SAMPLE_CHARS..] : recent;
        (domChar, ratio) = DominantChar(recent);
        return ratio > DOMINANCE && !char.IsWhiteSpace(domChar);
    }

    internal static bool IsToolLeak(string s) =>
        s.Contains("<tool_call") || s.Contains("<function=");

    internal static (char Char, double Ratio) DominantChar(string s)
    {
        if (string.IsNullOrEmpty(s)) return ('\0', 0);
        Dictionary<char, int> counts = new();
        foreach (char c in s) counts[c] = counts.TryGetValue(c, out int n) ? n + 1 : 1;
        KeyValuePair<char, int> top = counts.MaxBy(kv => kv.Value);
        return (top.Key, (double)top.Value / s.Length);
    }
}

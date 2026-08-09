namespace ARI.Listener;

/// <summary>
/// Turns a streamed response into speech chunks of 2-3 sentences (or more for very short sentences),
/// emitting only when a minimum character threshold is met. This produces more natural TTS output
/// because the synthesiser sees enough context to maintain consistent prosody, speed, and intonation
/// across sentence boundaries.
/// </summary>
internal sealed class SentenceChunker
{
    private const int MinSentenceChars = 12;
    private const int MinChunkChars = 80;
    private const int MaxChunkChars = 200;
    private const int MaxChunkSentences = 4;

    private readonly Action<string> onSentence;
    private string full = "";
    private int emitted = 0;
    private readonly List<string> pendingSentences = [];

    public SentenceChunker(Action<string> onSentence) => this.onSentence = onSentence;

    /// <summary>Feed the cumulative response text so far; emits any newly-completed chunks.</summary>
    public void Feed(string cumulative)
    {
        if (string.IsNullOrEmpty(cumulative)) return;
        if (cumulative.Length < emitted) { full = ""; emitted = 0; pendingSentences.Clear(); }
        full = cumulative;

        for (int i = emitted; i < full.Length; i++)
        {
            char c = full[i];
            if (c is not ('.' or '!' or '?' or '\n')) continue;
            if (i + 1 >= full.Length || !char.IsWhiteSpace(full[i + 1])) continue;

            string candidate = full.Substring(emitted, i - emitted + 1).Trim();
            if (candidate.Length < MinSentenceChars) continue;

            string clean = CleanMarkdown(candidate);
            if (clean.Length == 0) { emitted = i + 1; continue; }

            pendingSentences.Add(clean);
            emitted = i + 1;

            TryEmitChunk(force: false);
        }
    }

    /// <summary>Emit whatever remains as a final chunk (call when the stream ends).</summary>
    public void Flush()
    {
        if (emitted < full.Length)
        {
            string rest = CleanMarkdown(full.Substring(emitted).Trim());
            emitted = full.Length;
            if (rest.Length > 0) pendingSentences.Add(rest);
        }
        if (pendingSentences.Count > 0) TryEmitChunk(force: true);
    }

    private void TryEmitChunk(bool force)
    {
        int totalChars = 0;
        foreach (string s in pendingSentences) totalChars += s.Length;

        bool ready = force
            || totalChars >= MaxChunkChars
            || (totalChars >= MinChunkChars && pendingSentences.Count >= 2)
            || pendingSentences.Count >= MaxChunkSentences;

        if (!ready) return;

        string chunk = string.Join(" ", pendingSentences);
        pendingSentences.Clear();
        onSentence(chunk);
    }

    private static string CleanMarkdown(string sentence)
    {
        var sb = new System.Text.StringBuilder(sentence.Length);
        foreach (char c in sentence)
        {
            if (c == '*' || c == '`' || c == '#') continue;
            if (char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.OtherSymbol
                or System.Globalization.UnicodeCategory.Surrogate) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}

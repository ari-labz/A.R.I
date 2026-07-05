namespace ARI.Listener;

/// <summary>
/// Turns a streamed response into complete sentences, spoken as they finish. The pipeline's onDelta delivers
/// the CUMULATIVE text-so-far on every call (not incremental deltas), so this tracks how much has already been
/// emitted and only looks at the new tail. Splits on . ! ? / newline followed by whitespace, with a minimum
/// length so decimals / abbreviations don't split a sentence early.
/// </summary>
internal sealed class SentenceChunker
{
    private const int MinSentenceChars = 12;
    private readonly Action<string> onSentence;
    private string full = "";
    private int emitted = 0;   // chars of `full` already emitted as sentences

    public SentenceChunker(Action<string> onSentence) => this.onSentence = onSentence;

    /// <summary>Feed the cumulative response text so far; emits any newly-completed sentences.</summary>
    public void Feed(string cumulative)
    {
        if (string.IsNullOrEmpty(cumulative)) return;
        // A shorter string means a new/reset generation — start over.
        if (cumulative.Length < emitted) { full = ""; emitted = 0; }
        full = cumulative;

        for (int i = emitted; i < full.Length; i++)
        {
            char c = full[i];
            if (c is not ('.' or '!' or '?' or '\n')) continue;
            // require a following whitespace char (already arrived) so "3.14"/"e.g." mid-word doesn't trigger,
            // and a trailing "." at the current end waits for the next feed.
            if (i + 1 >= full.Length || !char.IsWhiteSpace(full[i + 1])) continue;

            string candidate = full.Substring(emitted, i - emitted + 1).Trim();
            if (candidate.Length < MinSentenceChars) continue;
            Emit(candidate);
            emitted = i + 1;
        }
    }

    /// <summary>Emit whatever remains as a final sentence (call when the stream ends).</summary>
    public void Flush()
    {
        if (emitted >= full.Length) return;
        string rest = full.Substring(emitted).Trim();
        emitted = full.Length;
        if (rest.Length > 0) Emit(rest);
    }

    private void Emit(string sentence)
    {
        string clean = sentence.Replace("*", "").Replace("`", "").Replace("#", "").Trim();
        if (clean.Length > 0) onSentence(clean);
    }
}

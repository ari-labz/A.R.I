using System.Text;
using System.Text.RegularExpressions;

namespace ARI.Voice;

public static partial class SentenceSplitter
{
    // Common abbreviations that should not end a sentence
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "vs", "etc", "inc", "ltd", "corp",
        "dept", "approx", "est", "e.g", "i.e", "fig", "vol", "no", "p", "pp"
    };

    public static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        string plain = StripMarkdown(text);
        return SplitSentences(plain);
    }

    private static string StripMarkdown(string text)
    {
        // Remove fenced code blocks entirely (not useful for TTS)
        text = FencedCodeBlock().Replace(text, " ");
        // Remove inline code
        text = InlineCode().Replace(text, m => m.Groups[1].Value);
        // Remove images
        text = MarkdownImage().Replace(text, "");
        // Replace links with their display text
        text = MarkdownLink().Replace(text, m => m.Groups[1].Value);
        // Remove heading markers
        text = Heading().Replace(text, "");
        // Remove bold/italic markers
        text = BoldItalic().Replace(text, m => m.Groups[1].Value);
        // Remove horizontal rules
        text = HorizontalRule().Replace(text, " ");
        // Remove blockquote markers
        text = BlockQuote().Replace(text, "");
        // Remove list markers (-, *, 1.)
        text = ListMarker().Replace(text, "");
        // Collapse excess whitespace / newlines to single spaces
        text = MultipleNewlines().Replace(text, ". ");
        text = MultipleSpaces().Replace(text, " ");

        return text.Trim();
    }

    private static IReadOnlyList<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var current   = new StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            current.Append(c);

            if (c is not ('.' or '!' or '?')) continue;

            // Ellipsis — keep going
            if (c == '.' && i + 1 < text.Length && text[i + 1] == '.') continue;

            // Check for abbreviation: word before this dot is a known abbreviation
            if (c == '.')
            {
                string word = WordBefore(text, i);
                if (Abbreviations.Contains(word)) continue;
                // Single letter (e.g. initials like "A.R.I.")
                if (word.Length == 1) continue;
            }

            // Must be followed by whitespace or end of string to count as sentence end
            int next = i + 1;
            if (next < text.Length && !char.IsWhiteSpace(text[next])) continue;

            string sentence = current.ToString().Trim();
            if (sentence.Length > 0) sentences.Add(sentence);
            current.Clear();
        }

        // Any remaining text
        string remaining = current.ToString().Trim();
        if (remaining.Length > 0) sentences.Add(remaining);

        return sentences.Where(s => s.Length > 1).ToList();
    }

    private static string WordBefore(string text, int dotIndex)
    {
        int end = dotIndex;
        while (end > 0 && !char.IsWhiteSpace(text[end - 1]) && text[end - 1] != '.') end--;
        return text[end..dotIndex];
    }

    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.Multiline)]
    private static partial Regex FencedCodeBlock();
    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();
    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownImage();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();
    [GeneratedRegex(@"^#{1,6}\s+", RegexOptions.Multiline)]
    private static partial Regex Heading();
    [GeneratedRegex(@"\*{1,3}([^*]+)\*{1,3}")]
    private static partial Regex BoldItalic();
    [GeneratedRegex(@"^[-*]{3,}\s*$", RegexOptions.Multiline)]
    private static partial Regex HorizontalRule();
    [GeneratedRegex(@"^>\s*", RegexOptions.Multiline)]
    private static partial Regex BlockQuote();
    [GeneratedRegex(@"^[\s]*[-*+]\s+|^\s*\d+\.\s+", RegexOptions.Multiline)]
    private static partial Regex ListMarker();
    [GeneratedRegex(@"\n{2,}")]
    private static partial Regex MultipleNewlines();
    [GeneratedRegex(@" {2,}")]
    private static partial Regex MultipleSpaces();
}

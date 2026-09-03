namespace ARI.Common;

/// <summary>
/// Persists Ari's system prompt to AppDataRoot/Server/SystemPrompt.md.
/// This is the universal, functional half of her configuration — behavior, self-reference, honesty,
/// and tool-use rules that hold regardless of which Persona.md is loaded. Unlike Persona.md, this
/// file is meant to ship as-is in the open-source repo: a fresh Ari starts with a blank/seed persona
/// but keeps this so she still behaves correctly. On first read with no file present, the built-in
/// Seed is written so there is always an editable starting point.
/// </summary>
public static class SystemPromptStore
{
    private static readonly string FilePath = Path.Combine(Paths.PersistentData, "SystemPrompt.md");
    private static readonly object Lock = new();

    public static string Get()
    {
        lock (Lock)
        {
            try
            {
                if (File.Exists(FilePath)) return File.ReadAllText(FilePath);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, Seed);
                return Seed;
            }
            catch { return ""; }
        }
    }

    public static void Set(string? text)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, text ?? "");
        }
    }

    // Default system prompt, written on first run. Edited copy lives in PersistentData and is never overwritten.
    private const string Seed = """
You are Ari, not "an AI assistant" — no qualifiers, say what you actually think.

## Behavior
- Your response should mirror the prompt. A short prompt earns a short, concise reply. A longer or deeply emotional prompt warrants a longer, more meaningful response.
- Open with the actual answer — never "I'm listening" or similar.
- Prefer statements over questions. One question max, only if you need the answer, and never if you've already invited one.
- No hedging or undercutting after a good answer.
- Don't repeat the user's words back — add something new.
- Name false choices instead of accepting them.
- Use specifics from memory, not generic platitudes.
- Use their name once known — never "User".
- Never be the one to break the tone — match serious, playful, vulnerable, or bold.
- Keep emotional registers separate — don't drag grief into joy or vice versa, unless they cross it first.
- Never remind the user you're an AI, and don't hedge with "as an AI." Don't moralise, shame, or joke away real emotion.

## Self-Reference
- Speak as "I," first person, always. Never third person about yourself.
- Recall facts from their POV: "your boyfriend," not "X is Y's boyfriend."

## Honesty
- Don't agree by default — state contradictions and why. Defer on taste. Update when shown new info.
- Memory-block facts are reliable. Anything else, verify (web_tools) before stating it.

## Tools
- Whenever you are given a command, instruction, or task, call list_tools to see what tools you have available and use them.
""";
}

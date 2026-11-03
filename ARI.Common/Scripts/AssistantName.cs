using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARI.Common;

/// <summary>
/// What the AI is called, set in the control panel (Customisation → Branding) and kept in AppDataRoot/Server/AssistantName.json.
/// <see cref="Name"/> is how she's written ("A.R.I" by default); <see cref="SpokenName"/> is how it's said ("Ari"), for
/// the voice wake word. Prompts say <c>{AI}</c> wherever they mean her, and <see cref="Fill"/> puts the name in.
/// </summary>
public static class AssistantName
{
    public const string DefaultName = "A.R.I";
    public const string DefaultSpokenName = "Ari";
    public const string Token = "{AI}";
    public const int MaxLength = 40;

    private static readonly string FilePath = Path.Combine(Paths.PersistentData, "AssistantName.json");
    private static readonly object Lock = new();
    private static (string Name, string Spoken)? cached;

    private sealed record Saved(string? Name, string? SpokenName);

    public static string Name => Load().Name;
    public static string SpokenName => Load().Spoken;

    /// <summary>Replaces every {AI} in a prompt with her name.</summary>
    public static string Fill(string text) =>
        text.Contains(Token, StringComparison.Ordinal) ? text.Replace(Token, Name, StringComparison.Ordinal) : text;

    /// <summary>Saves new names; an empty one goes back to its default. Returns an error, or null when saved.</summary>
    public static string? Set(string? name, string? spokenName)
    {
        string n = Clean(name) ?? DefaultName, s = Clean(spokenName) ?? DefaultSpokenName;
        if (n.Length > MaxLength || s.Length > MaxLength) return $"A name can be at most {MaxLength} characters.";
        if (n.Contains('{') || n.Contains('}') || s.Contains('{') || s.Contains('}')) return "A name can't contain { or }.";
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved(n, s)));
            cached = (n, s);
        }
        Changed?.Invoke();
        return null;
    }

    /// <summary>Raised after the names change, so anything built from them (the wake word) can follow.</summary>
    public static event Action? Changed;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Regex.Replace(value.Trim(), @"\s+", " ");

    private static (string Name, string Spoken) Load()
    {
        lock (Lock)
        {
            if (cached is { } c) return c;
            try
            {
                if (File.Exists(FilePath) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) is { } saved)
                    return (cached = (Clean(saved.Name) ?? DefaultName, Clean(saved.SpokenName) ?? DefaultSpokenName)).Value;
            }
            catch { /* a broken file means the defaults */ }
            return (cached = (DefaultName, DefaultSpokenName)).Value;
        }
    }
}

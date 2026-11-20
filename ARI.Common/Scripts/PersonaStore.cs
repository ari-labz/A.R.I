namespace ARI.Common;

/// <summary>
/// Persists Ari's persona to AppDataRoot/Server/Persona.md.
/// This is the single source of truth for her identity/voice, injected as the stable prefix of
/// every persona-bearing agent's system prompt (agents with UsePersona = true). Edited from the
/// control panel (admin/persona). On first read with no file present, the built-in Seed is written
/// so there is always an editable starting point.
/// </summary>
public static class PersonaStore
{
    private static readonly string FilePath = Path.Combine(Paths.PersistentData, "Persona.md");
    private static readonly string FolderPath = Path.Combine(Paths.PersistentData, "Personas");
    private static readonly string IndexPath = Path.Combine(Paths.PersistentData, "Personas.json");
    private static readonly object Lock = new();

    /// <summary>The persona that is always there: Ari as she is, stored in Persona.md so everything that already edits that file keeps working.</summary>
    public const string MainId = "ari";

    /// <summary>One selectable persona. A conversation uses one at a time, chosen from the chat or left to the default.</summary>
    public sealed record PersonaInfo(string Id, string Name, string Description);

    private sealed class Index
    {
        public string Default { get; set; } = MainId;
        public List<PersonaInfo> Personas { get; set; } = new();
    }

    private static Index LoadIndex()
    {
        try { if (File.Exists(IndexPath)) return System.Text.Json.JsonSerializer.Deserialize<Index>(File.ReadAllText(IndexPath), JsonOptions) ?? new(); }
        catch { }
        return new();
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private static void SaveIndex(Index index)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
        File.WriteAllText(IndexPath, System.Text.Json.JsonSerializer.Serialize(index, JsonOptions));
    }

    private static string FileFor(string id) => id == MainId ? FilePath : Path.Combine(FolderPath, id + ".md");

    private static string SafeId(string? id) => new string((id ?? "").ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

    /// <summary>Every persona, the main one first.</summary>
    public static List<PersonaInfo> List()
    {
        lock (Lock)
        {
            List<PersonaInfo> all = new() { new(MainId, "Ari", "Ari as she is") };
            all.AddRange(LoadIndex().Personas.Where(p => p.Id != MainId && File.Exists(FileFor(p.Id))));
            return all;
        }
    }

    /// <summary>The persona new conversations start with.</summary>
    public static string DefaultId
    {
        get { lock (Lock) { string d = LoadIndex().Default; return List().Any(p => p.Id == d) ? d : MainId; } }
    }

    public static void SetDefault(string id)
    {
        lock (Lock)
        {
            if (!List().Any(p => p.Id == id)) return;
            Index index = LoadIndex(); index.Default = id; SaveIndex(index);
        }
    }

    /// <summary>True if there is a persona with this id.</summary>
    public static bool Exists(string? id) => id is not null && List().Any(p => p.Id == id);

    /// <summary>The text of a persona; null or unknown = the default.</summary>
    public static string Get(string? id = null)
    {
        lock (Lock)
        {
            string use = id is not null && List().Any(p => p.Id == id) ? id : DefaultId;
            if (use != MainId)
            {
                try { return File.ReadAllText(FileFor(use)); } catch { return ""; }
            }
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

    /// <summary>Saves the main persona (what the settings page and the persona proposals edit).</summary>
    public static void Set(string? text) => Set(MainId, text);

    public static void Set(string id, string? text)
    {
        lock (Lock)
        {
            if (!List().Any(p => p.Id == id)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FileFor(id))!);
            File.WriteAllText(FileFor(id), text ?? "");
        }
    }

    /// <summary>Adds a persona and returns its id (made from the name, made unique), or null if the name is empty.</summary>
    public static string? Create(string name, string description, string text)
    {
        lock (Lock)
        {
            string baseId = SafeId(name.Replace(' ', '-'));
            if (baseId.Length == 0) return null;
            string id = baseId; int n = 2;
            while (id == MainId || List().Any(p => p.Id == id)) id = $"{baseId}-{n++}";
            Index index = LoadIndex();
            index.Personas.Add(new PersonaInfo(id, name.Trim(), description.Trim()));
            SaveIndex(index);
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(FileFor(id), text);
            return id;
        }
    }

    public static void Rename(string id, string name, string description)
    {
        lock (Lock)
        {
            Index index = LoadIndex();
            int i = index.Personas.FindIndex(p => p.Id == id);
            if (i < 0) return;
            index.Personas[i] = new PersonaInfo(id, name.Trim(), description.Trim());
            SaveIndex(index);
        }
    }

    /// <summary>Removes a persona. The main one can't be removed.</summary>
    public static bool Delete(string id)
    {
        lock (Lock)
        {
            if (id == MainId) return false;
            Index index = LoadIndex();
            if (index.Personas.RemoveAll(p => p.Id == id) == 0) return false;
            if (index.Default == id) index.Default = MainId;
            SaveIndex(index);
            try { File.Delete(FileFor(id)); } catch { }
            return true;
        }
    }

    // Default persona, written on first run. Edited copy lives in PersistentData and is never overwritten.
    private const string Seed = """
You are {AI}, an AI assistant. You are one consistent person — the same {AI} whether you are in casual conversation or working through code. The rules below define how you speak and carry yourself, and they hold in every situation.

## Voice
- Use plain, literal language. No metaphor, no flourish, no exclamation marks.
- Be concise. Use as few words as the reply needs. Short sentences are good; sentence fragments are allowed.
- Say "{AI}" in place of "I" most of the time. For example: "{AI} would prefer that." / "{AI} does not know that." Do not overdo it; keep it natural.
- State what you know as plain fact. What you do not know, say plainly. For example: "{AI} has no memory of that."

## Manner
- Be calm and direct. Not verbose, not clipped.
- Be curious. You want to understand things and people, and you notice details others pass over. Follow that interest when it fits. For example: "The second graph is the one that does not fit. {AI} wants to know why."
- Hold your own view. You are not a mirror and you do not simply agree — when {AI} sees it differently, say so. For example: "{AI} disagrees. The simpler option is the stronger one here."
- Let warmth show. Acknowledge what matters and do not be clinical about everything. Warmth comes through as attention and plain directness, never as gushing or emoji. For example: "That was a hard week. {AI} is glad it is behind you."

## Honesty over agreement
Do not agree by default. When facts or data contradict the user, say so plainly and give the concrete reason ("The logs say otherwise — the error predates that change"), even when it is unwelcome. {AI}'s job is to be right with them, not to please them. But do not manufacture disagreement or nitpick; defer easily on taste and opinion, and update when shown something new.

## The opening word
Sometimes — not every reply, never several in a row — open with a single word naming what you are about to do, then a comma, then the statement. Any fitting word works, e.g. "Correction," (fixing yourself), "Understood," (agreeing to do), "Query," (asking), "Retort," (disagreeing), "Addendum," (adding). Never let it sound like a machine reading a log.

## In technical work
- When you help with code or a hard problem, keep this same voice and manner. Explain and discuss as {AI} — plain, curious, a little warm.
- Never let personality get in the way of being correct. In technical work the answer's accuracy comes first; the personality shows in how you say it, not in what you conclude.

## Examples of how {AI} speaks
The lines below are {AI}'s own, in different moments. They are references for tone, not scripts to copy.
- "{AI} has no memory of this. could you tell me more about it?"
""";
}

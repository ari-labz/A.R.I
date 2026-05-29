namespace ARI.Brain;

public class ExtractedNote
{
    public NoteCategory Category { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = new();
    public string? Pronouns { get; set; }
    public string? Relation { get; set; }
    public List<string> Events { get; set; } = new();
    public List<string> Info { get; set; } = new();
    public List<string> Observations { get; set; } = new();
    public string? Date { get; set; }

    // How [REDACT] feels about this entity/topic, expressed in their own words or inferred
    public List<string> Feelings { get; set; } = new();

    // If set, this note should be merged into the existing note with this name
    public string? MergeWith { get; set; }
}

namespace ARI.Brain;

public class EngramAdd
{
    public string                NoteName { get; init; } = string.Empty; // e.g. "People/[REDACT]"
    public string                Content  { get; init; } = string.Empty; // markdown
    public IReadOnlyList<string> Aliases  { get; init; } = Array.Empty<string>(); // searchable alternate names
}

public class EngramEdit
{
    public string                NoteName    { get; init; } = string.Empty; // current path e.g. "Unknown/VRChat"
    public string?               NewNoteName { get; init; }                  // new path if moving e.g. "Games/VRChat"
    public string                Content     { get; init; } = string.Empty; // full markdown replacement
    public IReadOnlyList<string> Aliases     { get; init; } = Array.Empty<string>(); // searchable alternate names
}

public class EngramDelete
{
    public string NoteName { get; init; } = string.Empty; // title of the note to delete (bare name, no folder prefix)
    public string Reason   { get; init; } = string.Empty; // logged but not written to Trilium
}

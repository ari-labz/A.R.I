namespace ARI.Brain;

public class EngramAdd
{
    public string NoteName { get; init; } = string.Empty; // e.g. "People/[REDACT]"
    public string Content  { get; init; } = string.Empty; // markdown
}

public class EngramEdit
{
    public string  NoteName    { get; init; } = string.Empty; // current path e.g. "Unknown/VRChat"
    public string? NewNoteName { get; init; }                  // new path if moving e.g. "Games/VRChat"
    public string  Content     { get; init; } = string.Empty; // full markdown replacement
}

namespace ARI.LLM;

/// <summary>A single note addition or edit recorded during an Engram sweep.</summary>
public record NoteChange(string Title, string? Url, string Op, string Summary);

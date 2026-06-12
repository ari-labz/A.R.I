namespace ARI.LLM;

/// <summary>The pipeline a thread belongs to. Determines how its prompts are processed.</summary>
public enum ThreadType
{
    Dialogue,
    Code,
    Memory,
    Engram,
    Context,
    Refactor,
    Classifier
}

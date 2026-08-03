namespace ARI.LLM;

// Text-conversation agent owned by DialoguePipeline.
// Inherits all Dialogue behaviour; the clean subclass makes the pipeline ownership explicit and
// provides a natural extension point for text-specific overrides without Mode conditionals.
internal sealed class TextingAgent : Dialogue { }

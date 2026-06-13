using System.Text.Json.Serialization;

namespace ARI.LLM;

/// <summary>
/// Base class for everything that appears in a thread's display history.
/// Clients receive a typed list of ThreadItems and render each type differently.
/// LLM agents never see ThreadItems — they receive ChatHistory derived from them.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UserMessage),     "userMessage")]
[JsonDerivedType(typeof(AriResponse),     "ariResponse")]
[JsonDerivedType(typeof(CommandInput),    "commandInput")]
[JsonDerivedType(typeof(CommandResponse), "commandResponse")]
[JsonDerivedType(typeof(EngramEvent),     "engramEvent")]
public abstract class ThreadItem
{
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>
    /// The text this item contributes to the LLM context window.
    /// Null means this item is UI-only and is never passed to any LLM.
    /// </summary>
    [JsonIgnore]
    public virtual string? Message => null;

    /// <summary>
    /// The text this item contributes to the LLM context window on *subsequent* turns,
    /// with any UI-only display markup removed. Defaults to <see cref="Message"/>; types
    /// that embed render markers (e.g. tool-use cards) override this to strip them so the
    /// model never sees — and never learns to imitate — the markup.
    /// </summary>
    [JsonIgnore]
    public virtual string? ContextText => Message;

    /// <summary>
    /// The speaker name used when formatting this item for the LLM.
    /// e.g. "xywren" for a user, "ARI" for a response.
    /// </summary>
    [JsonIgnore]
    public virtual string AuthorName => string.Empty;
}

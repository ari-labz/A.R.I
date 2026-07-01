namespace ARI.LLM;

/// <summary>Lifecycle state shared by a <see cref="Response"/> and a <see cref="ContentBlock"/>.
/// A block/response starts <see cref="Streaming"/>; a card's <see cref="Card.Flip"/> moves it to
/// <see cref="Complete"/>. <see cref="Cancelled"/> is only meaningful for a <see cref="Response"/>.</summary>
public enum State { Streaming, Complete, Error, Cancelled }

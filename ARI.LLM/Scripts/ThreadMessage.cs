namespace ARI.LLM;

/// <summary>
/// A single entry in the LLM context window, derived from ThreadHistory by GetChatHistory().
/// Never stored — always computed on demand. LLMs see these; clients never do.
/// Role is "user" for all humans, "assistant" for ARI.
/// <para><see cref="ToolBatches"/> carries the tool calls an ARI turn made, in order and batched as they ran,
/// with results shrunk to stubs — replayed as real tool calls so her history shows her checking things
/// rather than stating results out of nowhere. Null for anything else.</para>
/// </summary>
public record ThreadMessage(string Role, string Username, string Content,
    IReadOnlyList<IReadOnlyList<HistoryToolCall>>? ToolBatches = null);

/// <summary>One past tool call as replayed into context: name, (shrunk) arguments, and a stub of its result.</summary>
public record HistoryToolCall(string Name, string Args, string Result);

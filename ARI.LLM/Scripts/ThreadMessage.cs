namespace ARI.LLM;

/// <summary>
/// A single entry in the LLM context window, derived from ThreadHistory by GetChatHistory().
/// Never stored — always computed on demand. LLMs see these; clients never do.
/// Role is "user" for all humans, "assistant" for ARI.
/// </summary>
public record ThreadMessage(string Role, string Username, string Content);

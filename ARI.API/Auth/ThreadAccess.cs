using System.Security.Claims;
using ARI.LLM;

namespace ARI.API.Auth;

/// <summary>The one rule for who may touch a thread, for code that isn't a ThreadsController action (the raw WebSockets).</summary>
public static class ThreadAccess
{
    /// <summary>Admins can use any thread; everyone else only threads they own. A key with no thread yet is open —
    /// creating it makes the caller its owner.</summary>
    public static bool IsClientKey(string key) => key.StartsWith("web-", StringComparison.Ordinal) || key.StartsWith("client-", StringComparison.Ordinal);

    public static bool CanUse(ClaimsPrincipal user, LLMModule llm, string threadKey)
    {
        if (user.FindFirstValue(ClaimTypes.Role) == Roles.Admin) return true;
        // A thread that doesn't exist yet can only be claimed under a key the server hands out; the names that
        // integrations use (Discord conversations, say) can't be taken first by someone else.
        if (!llm.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? thread))
        {
            // Saved (Persistent Threads) conversations still belong to their owner.
            if (llm.GetPersistedInfo(threadKey) is { } saved) return saved.OwnerId is not null && saved.OwnerId == user.FindFirstValue(ClaimTypes.NameIdentifier);
            return IsClientKey(threadKey);
        }
        string? id = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return id is not null && thread.OwnerId == id;
    }
}

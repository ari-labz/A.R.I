using System.Security.Claims;
using ARI.LLM;

namespace ARI.API.Auth;

/// <summary>The one rule for who may touch a thread, for code that isn't a ThreadsController action (the raw WebSockets).</summary>
public static class ThreadAccess
{
    /// <summary>Admins can use any thread; everyone else only threads they own. A key with no thread yet is open —
    /// creating it makes the caller its owner.</summary>
    public static bool CanUse(ClaimsPrincipal user, LLMModule llm, string threadKey)
    {
        if (!llm.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? thread)) return true;
        if (user.FindFirstValue(ClaimTypes.Role) == Roles.Admin) return true;
        string? id = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return id is not null && thread.OwnerId == id;
    }
}

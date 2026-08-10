using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>
/// The approval gate on persona self-edits. Ari can propose a change to how she carries herself; only a
/// call to approve here ever writes Persona.md. Kept off the /admin surface deliberately — this is the
/// user answering a question in their own conversation, not an administrative action.
/// </summary>
[Route("persona/proposals")]
[ApiController]
public class PersonaProposalsController : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    [HttpGet("{id}")]
    public IActionResult Get(string id)
        => PersonaProposalStore.Find(id) is { } p ? Ok(p) : NotFound();

    /// <summary>
    /// Applies the proposed change. The next turn in any thread rebuilds its system prompt from the file,
    /// so the new persona is live immediately — and because the persona sits at the very top of that
    /// prompt, the change moves the cache prefix and the thread re-prefills on its own.
    /// </summary>
    [HttpPost("{id}/approve")]
    public IActionResult Approve(string id)
    {
        (bool ok, string message) = PersonaProposalStore.Approve(id);
        if (ok) Refresh(id);
        return ok ? Ok(new { ok = true, message, persona = PersonaStore.Get() })
                  : BadRequest(new { ok = false, message });
    }

    [HttpPost("{id}/reject")]
    public IActionResult Reject(string id)
    {
        if (!PersonaProposalStore.Reject(id)) return BadRequest(new { ok = false, message = "Nothing pending to reject." });
        Refresh(id);
        return Ok(new { ok = true });
    }

    /// <summary>Nudges the proposing thread so its watchers re-fetch and the card repaints as decided.</summary>
    private void Refresh(string id)
    {
        if (PersonaProposalStore.Find(id) is { } proposal) Llm?.NotifyThreadUpdated(proposal.ThreadKey);
    }
}

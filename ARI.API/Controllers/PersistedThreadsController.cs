using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>The saved conversations that Persistent Threads produces (how many, how big, delete them all). Whether the
/// feature is on is a setting: <c>persistedThreadsEnabled</c> at /admin/settings. Under /admin/, so admin only.</summary>
[Route("api/v1/admin/persistedThreads")]
[ApiController]
public class PersistedThreadsController : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    [HttpGet]
    public IActionResult Get()
    {
        (int count, long bytes) = Llm?.PersistedStats() ?? (0, 0);
        return Ok(new { count, bytes });
    }

    /// <summary>Deletes every saved conversation for good.</summary>
    [HttpDelete]
    public IActionResult DeleteAll()
    {
        if (Llm is null) return StatusCode(503, new { error = "ARI is not ready yet." });
        return Ok(new { ok = true, deleted = Llm.DeleteAllPersisted() });
    }
}

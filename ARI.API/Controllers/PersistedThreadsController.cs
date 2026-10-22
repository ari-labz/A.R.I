using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>The Persistent Threads setting and the saved conversations it produces. Under /admin/, so admin only.</summary>
[Route("admin/persisted-threads")]
[ApiController]
public class PersistedThreadsController : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    public record SetRequest(bool Enabled);

    [HttpGet]
    public IActionResult Get()
    {
        (int count, long bytes) = Llm?.PersistedStats() ?? (0, 0);
        return Ok(new { enabled = PersistedThreadsSetting.Enabled, count, bytes });
    }

    [HttpPut]
    public IActionResult Set([FromBody] SetRequest req)
    {
        PersistedThreadsSetting.Set(req.Enabled);
        (int count, long bytes) = Llm?.PersistedStats() ?? (0, 0);
        return Ok(new { enabled = req.Enabled, count, bytes });
    }

    /// <summary>Deletes every saved conversation for good.</summary>
    [HttpDelete]
    public IActionResult DeleteAll()
    {
        if (Llm is null) return StatusCode(503, new { error = "ARI is not ready yet." });
        return Ok(new { deleted = Llm.DeleteAllPersisted() });
    }
}

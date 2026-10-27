using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>The C# scripts that act as pipeline nodes. Saving one compiles it straight away and, if it compiles, the next run of any
/// node using it runs the new version, with no restart. Scripts are trusted code with the same access as A·R·I itself, so this is
/// admin-only like the rest of /admin.</summary>
[Route("api/v1/admin/scripts")]
[ApiController]
public class ScriptsController : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    public sealed record SaveScriptRequest(string? Source);

    [HttpGet]
    public IActionResult List() => Ok((Llm?.ListScripts() ?? []).Select(s => new
    {
        name = s.Name, description = s.Description, inputs = s.Inputs, optionalInputs = s.OptionalInputs, outputs = s.Outputs, error = s.Error,
    }));

    [HttpGet("{name}")]
    public IActionResult Get(string name)
    {
        string? source = Llm?.ScriptSource(name);
        return source is null ? NotFound(new { error = "No such script." }) : Ok(new { name, source });
    }

    /// <summary>Compiles and saves. A script that doesn't compile is not saved and the old one keeps running; the errors come back in <c>error</c>.</summary>
    [HttpPut("{name}")]
    public IActionResult Save(string name, [FromBody] SaveScriptRequest req)
    {
        if (Llm is null) return StatusCode(503, new { error = "ARI is not ready yet." });
        if (string.IsNullOrWhiteSpace(req?.Source)) return BadRequest(new { error = "source is required." });
        if (req.Source.Length > 200_000) return BadRequest(new { error = "That script is too long." });
        string? error = Llm.SaveScript(name, req.Source);
        return error is null ? Ok(new { ok = true }) : BadRequest(new { error });
    }

    [HttpDelete("{name}")]
    public IActionResult Delete(string name) =>
        Llm?.DeleteScript(name) == true ? Ok(new { ok = true }) : NotFound(new { error = "No such script." });
}

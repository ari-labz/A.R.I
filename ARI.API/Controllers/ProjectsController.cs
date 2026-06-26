using ARI.API;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

[Route("projects")]
[ApiController]
public class ProjectsController(ProjectStore store) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll() => Ok(store.GetAll());

    [HttpPost]
    public IActionResult Create([FromBody] CreateProjectRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name is required." });

        Project project = new(
            Id:                Guid.NewGuid().ToString("N"),
            Name:              req.Name.Trim(),
            Description:       req.Description?.Trim() ?? "",
            Instructions:      req.Instructions?.Trim() ?? "",
            CreatedAt:         DateTime.UtcNow,
            ForceCodePipeline: req.ForceCodePipeline ?? true);

        store.Add(project);
        return Ok(project);
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] CreateProjectRequest req)
    {
        Project? existing = store.Get(id);
        if (existing is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name is required." });

        Project updated = existing with
        {
            Name              = req.Name.Trim(),
            Description       = req.Description?.Trim() ?? "",
            Instructions      = req.Instructions?.Trim() ?? "",
            ForceCodePipeline = req.ForceCodePipeline ?? existing.ForceCodePipeline,
        };
        store.Update(updated);
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        if (store.Get(id) is null) return NotFound();
        store.Delete(id);
        return Ok();
    }

    // ── Project attachments ───────────────────────────────────────────────────────

    [HttpGet("{id}/attachments")]
    public IActionResult GetAttachments(string id)
    {
        if (store.Get(id) is null) return NotFound();
        return Ok(store.GetAttachmentNames(id).Select(n => new { name = n }));
    }

    [HttpPost("{id}/attachments")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> AddAttachment(string id, IFormFile file)
    {
        if (store.Get(id) is null) return NotFound();
        if (file is null || file.Length == 0) return BadRequest("No file provided.");

        using MemoryStream ms = new();
        await file.CopyToAsync(ms);
        store.SaveAttachment(id, file.FileName, ms.ToArray());
        return Ok(new { name = file.FileName });
    }

    [HttpDelete("{id}/attachments/{name}")]
    public IActionResult DeleteAttachment(string id, string name)
    {
        if (store.Get(id) is null) return NotFound();
        store.DeleteAttachment(id, name);
        return Ok();
    }
}

public record CreateProjectRequest(string Name, string? Description, string? Instructions, bool? ForceCodePipeline);

using ARI.Common;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>The personas Ari can speak in. Anyone signed in can list them; the owner edits them under /admin/personas.</summary>
[ApiController]
public class PersonasController : ControllerBase
{
    public sealed record PersonaBody(string? Name, string? Description, string? Text);
    public sealed record ChoiceBody(string? PersonaId);

    [HttpGet("api/v1/personas")]
    public IActionResult List() => Ok(new { defaultId = PersonaStore.DefaultId, personas = PersonaStore.List() });

    [HttpGet("api/v1/admin/personas/{id}")]
    public IActionResult Get(string id)
        => PersonaStore.Exists(id) ? Ok(new { id, text = PersonaStore.Get(id), info = PersonaStore.List().First(p => p.Id == id) }) : NotFound();

    [HttpPost("api/v1/admin/personas")]
    public IActionResult Create([FromBody] PersonaBody body)
    {
        string? id = PersonaStore.Create(body?.Name ?? "", body?.Description ?? "", body?.Text ?? "");
        return id is null ? BadRequest(new { error = "Give the persona a name." }) : Ok(new { id });
    }

    [HttpPut("api/v1/admin/personas/{id}")]
    public IActionResult Update(string id, [FromBody] PersonaBody body)
    {
        if (!PersonaStore.Exists(id)) return NotFound();
        if (body?.Text is not null) PersonaStore.Set(id, body.Text);
        if (id != PersonaStore.MainId && (body?.Name is not null || body?.Description is not null))
        {
            ARI.Common.PersonaStore.PersonaInfo current = PersonaStore.List().First(p => p.Id == id);
            PersonaStore.Rename(id, body.Name ?? current.Name, body.Description ?? current.Description);
        }
        return Ok();
    }

    [HttpDelete("api/v1/admin/personas/{id}")]
    public IActionResult Delete(string id) => PersonaStore.Delete(id) ? Ok() : NotFound();

    /// <summary>Which persona new conversations start with.</summary>
    [HttpPut("api/v1/admin/personas-default")]
    public IActionResult SetDefault([FromBody] ChoiceBody body)
    {
        if (!PersonaStore.Exists(body?.PersonaId)) return NotFound();
        PersonaStore.SetDefault(body!.PersonaId!);
        return Ok();
    }
}

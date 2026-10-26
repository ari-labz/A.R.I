using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

[ApiController]
public class BrandingController : ControllerBase
{
    // Public (AuthMiddleware.AnonPaths): the UI needs it before sign-in to style the sidebar logo.
    [HttpGet("api/v1/branding")]
    public IActionResult State() => Ok(new { sidebarDerived = BrandingStore.SidebarDerived });

    [HttpGet("api/v1/admin/branding")]
    public IActionResult List() => Ok(BrandingStore.Slots.Select(s => new
    {
        s.Key, s.Label, s.Used, s.DefaultPath,
        Custom = BrandingStore.Custom(s.Key) is not null,
        Derived = BrandingStore.Resolve(s.Key) is { Derived: true },
        Version = BrandingStore.Version(s.Key)?.Ticks ?? 0,
    }));

    [HttpPost("api/v1/admin/branding/{slot}")]
    [RequestSizeLimit(3 * 1024 * 1024)]
    public IActionResult Upload(string slot, IFormFile file)
    {
        if (BrandingStore.Find(slot) is null) return NotFound();
        using Stream s = file.OpenReadStream();
        string? err = BrandingStore.Save(slot, s);
        return err is null ? Ok(new { ok = true }) : BadRequest(new { error = err });
    }

    [HttpDelete("api/v1/admin/branding/{slot}")]
    public IActionResult Reset(string slot)
    {
        if (BrandingStore.Find(slot) is null) return NotFound();
        BrandingStore.Reset(slot);
        return Ok(new { ok = true });
    }
}

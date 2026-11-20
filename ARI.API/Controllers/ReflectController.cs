using ARI.LLM;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>The reflect stage's settings (see <see cref="Reflector"/>): whether replies are reviewed, and how an edit looks on screen.</summary>
[ApiController]
public class ReflectController : ControllerBase
{
    public sealed record Patch(bool? Enabled, string? EditStyle, int? MinChars, bool? VerifyWork);

    [HttpGet("api/v1/admin/reflect")]
    public IActionResult Get()
    {
        ReflectSettings.Values v = ReflectSettings.Load();
        return Ok(new { enabled = v.Enabled, editStyle = v.EditStyle, minChars = v.MinChars, verifyWork = v.VerifyWork, styles = ReflectSettings.EditStyles });
    }

    [HttpPut("api/v1/admin/reflect")]
    public IActionResult Put([FromBody] Patch body)
    {
        ReflectSettings.Values v = ReflectSettings.Load();
        if (body.Enabled is { } e) v.Enabled = e;
        if (body.EditStyle is { } s && ReflectSettings.EditStyles.Contains(s)) v.EditStyle = s;
        if (body.MinChars is { } m && m >= 0) v.MinChars = m;
        if (body.VerifyWork is { } w) v.VerifyWork = w;
        ReflectSettings.Save(v);
        return Ok();
    }
}

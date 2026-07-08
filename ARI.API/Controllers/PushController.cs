using ARI.Common;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>
/// Web Push subscription management for the PWA. The client fetches the VAPID public key, subscribes
/// through the browser, and posts the resulting subscription here so Ari can push notifications to it.
/// </summary>
[Route("push")]
[ApiController]
public class PushController : ControllerBase
{
    private static IWebPushModule? Push => Modules.WebPush;

    public sealed record SubscriptionKeys(string P256dh, string Auth);
    public sealed record SubscribeRequest(string Endpoint, SubscriptionKeys Keys);
    public sealed record UnsubscribeRequest(string Endpoint);

    /// <summary>The VAPID public key the browser needs to call pushManager.subscribe.</summary>
    [HttpGet("vapid-public-key")]
    public IActionResult GetVapidPublicKey()
    {
        if (Push is null) return StatusCode(503, "Push is not ready yet.");
        return Ok(new { publicKey = Push.VapidPublicKey });
    }

    [HttpPost("subscribe")]
    public IActionResult Subscribe([FromBody] SubscribeRequest req)
    {
        if (Push is null) return StatusCode(503, "Push is not ready yet.");
        if (string.IsNullOrWhiteSpace(req.Endpoint) || req.Keys is null
            || string.IsNullOrWhiteSpace(req.Keys.P256dh) || string.IsNullOrWhiteSpace(req.Keys.Auth))
            return BadRequest("Missing endpoint or keys.");

        Push.AddSubscription(req.Endpoint, req.Keys.P256dh, req.Keys.Auth);
        return Ok(new { ok = true });
    }

    [HttpPost("unsubscribe")]
    public IActionResult Unsubscribe([FromBody] UnsubscribeRequest req)
    {
        if (Push is null) return StatusCode(503, "Push is not ready yet.");
        if (!string.IsNullOrWhiteSpace(req.Endpoint)) Push.RemoveSubscription(req.Endpoint);
        return Ok(new { ok = true });
    }
}

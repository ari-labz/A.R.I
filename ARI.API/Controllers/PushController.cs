using System.Security.Claims;
using ARI.Common;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>
/// Web Push subscription management for the PWA. The client fetches the VAPID public key, subscribes
/// through the browser, and posts the resulting subscription here so Ari can push notifications to it.
/// </summary>
[Route("api/v1/push")]
[ApiController]
public class PushController : ControllerBase
{
    private static IWebPushModule? Push => Modules.WebPush;

    public sealed record SubscriptionKeys(string P256dh, string Auth);
    public sealed record SubscribeRequest(string Endpoint, SubscriptionKeys Keys);
    public sealed record UnsubscribeRequest(string Endpoint);

    /// <summary>The VAPID public key the browser needs to call pushManager.subscribe.</summary>
    [HttpGet("key")]
    public IActionResult GetVapidPublicKey()
    {
        if (Push is null) return StatusCode(503, "Push is not ready yet.");
        return Ok(new { publicKey = Push.VapidPublicKey });
    }

    private static readonly string[] PushHosts = ["fcm.googleapis.com", "updates.push.services.mozilla.com", "push.apple.com", "notify.windows.com", "push.services.mozilla.com"];

    private static bool IsPushServiceHost(string host)
        => PushHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    [HttpPost("subscriptions")]
    public IActionResult Subscribe([FromBody] SubscribeRequest req)
    {
        if (Push is null) return StatusCode(503, "Push is not ready yet.");
        if (string.IsNullOrWhiteSpace(req.Endpoint) || req.Keys is null
            || string.IsNullOrWhiteSpace(req.Keys.P256dh) || string.IsNullOrWhiteSpace(req.Keys.Auth))
            return BadRequest("Missing endpoint or keys.");

        // Only the push services browsers use: the server will send a request to this address, so it must never be an
        // address inside the network.
        if (!Uri.TryCreate(req.Endpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme != Uri.UriSchemeHttps || !IsPushServiceHost(endpoint.Host))
            return BadRequest(new { error = "That is not a browser push service address." });

        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int userId)) return Unauthorized();
        Push.AddSubscription(req.Endpoint, req.Keys.P256dh, req.Keys.Auth, userId, User.IsInRole(ARI.API.Auth.Roles.Admin));
        return Ok(new { ok = true });
    }

    [HttpDelete("subscriptions")]
    public IActionResult Unsubscribe([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] UnsubscribeRequest? body, [FromQuery] string? endpoint = null)
    {
        // The address of the subscription can come in the body or as ?endpoint= (some proxies drop a body on DELETE).
        UnsubscribeRequest req = new(body?.Endpoint ?? endpoint ?? "");
        if (Push is null) return StatusCode(503, "Push is not ready yet.");
        if (int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int userId) && !string.IsNullOrWhiteSpace(req.Endpoint))
            Push.RemoveSubscription(req.Endpoint, userId);
        return Ok(new { ok = true });
    }
}

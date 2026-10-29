using System.Collections.Concurrent;
using System.Text.Json;
using ARI.Common;
using Microsoft.Extensions.Logging;
using WebPush;

namespace ARI.API;

/// <summary>
/// Web Push delivery for the PWA. Owns the VAPID keypair and the set of browser push subscriptions,
/// both persisted under ~/.ari/Server. Ari's proactive path calls <see cref="SendPushNotification"/> to
/// ring the phone of the person a thread belongs to; the notification body carries the message and (optionally) a deep-link URL.
/// </summary>
public sealed class WebPushModule : IWebPushModule
{
    // UserId/IsAdmin say whose device this is, so a push only goes to the person it is for. A record saved before
    // these existed has UserId 0 and is dropped on load: nobody can tell whose it was.
    private sealed record StoredSubscription(string Endpoint, string P256dh, string Auth, int UserId = 0, bool IsAdmin = false);
    private sealed record VapidKeys(string PublicKey, string PrivateKey);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly ILogger<WebPushModule> log;
    private readonly string _subsPath;
    private readonly string subject;               // mailto: or https: contact, required by the VAPID spec
    private readonly VapidDetails _vapid;
    private readonly WebPushClient _client = new();
    private readonly ConcurrentDictionary<string, StoredSubscription> _subs = new();
    private readonly object _saveLock = new();

    public string VapidPublicKey => _vapid.PublicKey;

    public WebPushModule(ILogger<WebPushModule> log, string storageDir, string subject)
    {
        this.log = log;
        this.subject = string.IsNullOrWhiteSpace(subject) ? "mailto:owner@a-r-i.ai" : subject;
        Directory.CreateDirectory(storageDir);
        _subsPath = Path.Combine(storageDir, "PushSubscriptions.json");

        VapidKeys keys = LoadOrCreateVapidKeys(Path.Combine(storageDir, "VapidKeys.json"));
        _vapid = new VapidDetails(subject, keys.PublicKey, keys.PrivateKey);

        List<StoredSubscription> loaded = LoadSubscriptions();
        foreach (StoredSubscription s in loaded.Where(x => x.UserId > 0))
            _subs[s.Endpoint] = s;
        if (loaded.Count != _subs.Count)
        {
            log.LogWarning("[WebPush] dropped {Count} subscription(s) with no owner — those devices re-subscribe the next time their user signs in.", loaded.Count - _subs.Count);
            Save();
        }
        log.LogInformation("[WebPush] ready — {Count} subscription(s) loaded.", _subs.Count);
    }

    public void AddSubscription(string endpoint, string p256dh, string auth, int userId, bool isAdmin)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || userId <= 0) return;
        // Keyed by endpoint, so a different user signing in on the same browser takes the device over.
        _subs[endpoint] = new StoredSubscription(endpoint, p256dh, auth, userId, isAdmin);
        Save();
        log.LogInformation("[WebPush] subscription registered ({Count} total).", _subs.Count);
    }

    public void RemoveSubscription(string endpoint, int userId)
    {
        // Only the device's current owner can remove it.
        if (_subs.TryGetValue(endpoint, out StoredSubscription? sub) && sub.UserId == userId && _subs.TryRemove(endpoint, out _)) { Save(); log.LogInformation("[WebPush] subscription removed ({Count} left).", _subs.Count); }
    }

    public async Task SendPushNotification(string text, string? url = null, string? title = null, string? ownerUserId = null, string? tag = null)
    {
        // A push goes to the devices of the person it is for: a thread's owner. A thread with no owner (a public Discord
        // chat, an integration) has nobody to ring, so it sends nothing.
        StoredSubscription[] targets = int.TryParse(ownerUserId, out int owner)
            ? _subs.Values.Where(s => s.UserId == owner).ToArray()
            : [];
        if (targets.Length == 0) { log.LogInformation("[WebPush] no matching subscription — nothing to notify."); return; }

        string payload = JsonSerializer.Serialize(new
        {
            title = string.IsNullOrWhiteSpace(title) ? "Ari" : title,
            body  = text,
            url,
            tag,
        });

        log.LogInformation("[WebPush] sending to {Count} subscription(s)…", targets.Length);
        List<string> dead = new();
        foreach (StoredSubscription s in targets)
        {
            // Last path segment (the token) is enough to tell subscriptions apart in the log without
            // dumping the full endpoint URL every time.
            string device = s.Endpoint.Length > 24 ? s.Endpoint[^24..] : s.Endpoint;
            try
            {
                await _client.SendNotificationAsync(new PushSubscription(s.Endpoint, s.P256dh, s.Auth), payload, _vapid);
                // A 2xx from the push service means it accepted the message for delivery — that's as
                // far as the server can ever confirm; whether the OS/browser actually surfaces it on
                // the device is outside this process (battery optimization, the PWA not being
                // installed to the home screen, notifications disabled at the OS level, etc.).
                log.LogInformation("[WebPush] accepted by push service ({Tag}).", device);
            }
            catch (WebPushException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Gone
                                                            or System.Net.HttpStatusCode.NotFound)
            {
                // 404/410 — the browser dropped this subscription; prune it.
                log.LogWarning("[WebPush] subscription gone ({Tag}, {Status}) — pruning.", device, ex.StatusCode);
                dead.Add(s.Endpoint);
            }
            catch (Exception ex)
            {
                log.LogWarning("[WebPush] send failed ({Tag}): {Msg}", device, ex.Message);
            }
        }

        if (dead.Count > 0)
        {
            foreach (string endpoint in dead) _subs.TryRemove(endpoint, out _);
            Save();
            log.LogInformation("[WebPush] pruned {Count} expired subscription(s).", dead.Count);
        }
    }

    // ── Persistence ──────────────────────────────────────────────────────────────

    private VapidKeys LoadOrCreateVapidKeys(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                VapidKeys? existing = JsonSerializer.Deserialize<VapidKeys>(File.ReadAllText(path), JsonOpts);
                if (existing is { PublicKey.Length: > 0, PrivateKey.Length: > 0 }) return existing;
            }
        }
        catch (Exception ex) { log.LogWarning("[WebPush] could not read VAPID keys ({Msg}); regenerating.", ex.Message); }

        VapidDetails generated = VapidHelper.GenerateVapidKeys();
        VapidKeys keys = new(generated.PublicKey, generated.PrivateKey);
        try { File.WriteAllText(path, JsonSerializer.Serialize(keys, JsonOpts)); }
        catch (Exception ex) { log.LogWarning("[WebPush] could not persist VAPID keys: {Msg}", ex.Message); }
        log.LogInformation("[WebPush] generated a new VAPID keypair.");
        return keys;
    }

    private List<StoredSubscription> LoadSubscriptions()
    {
        try
        {
            if (File.Exists(_subsPath))
                return JsonSerializer.Deserialize<List<StoredSubscription>>(File.ReadAllText(_subsPath), JsonOpts) ?? new();
        }
        catch (Exception ex) { log.LogWarning("[WebPush] could not read subscriptions ({Msg}).", ex.Message); }
        return new();
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try { File.WriteAllText(_subsPath, JsonSerializer.Serialize(_subs.Values.ToList(), JsonOpts)); }
            catch (Exception ex) { log.LogWarning("[WebPush] could not persist subscriptions: {Msg}", ex.Message); }
        }
    }
}

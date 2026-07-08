using System.Collections.Concurrent;
using System.Text.Json;
using ARI.Common;
using Microsoft.Extensions.Logging;
using WebPush;

namespace ARI.API;

/// <summary>
/// Web Push delivery for the PWA. Owns the VAPID keypair and the set of browser push subscriptions,
/// both persisted under ~/.ari/Server. Ari's proactive path calls <see cref="SendPushNotification"/> to
/// ring the owner's phone; the notification body carries the message and (optionally) a deep-link URL.
/// </summary>
public sealed class WebPushModule : IWebPushModule
{
    private sealed record StoredSubscription(string Endpoint, string P256dh, string Auth);
    private sealed record VapidKeys(string PublicKey, string PrivateKey);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly ILogger<WebPushModule> _log;
    private readonly string _subsPath;
    private readonly string _subject;               // mailto: or https: contact, required by the VAPID spec
    private readonly VapidDetails _vapid;
    private readonly WebPushClient _client = new();
    private readonly ConcurrentDictionary<string, StoredSubscription> _subs = new();
    private readonly object _saveLock = new();

    public string VapidPublicKey => _vapid.PublicKey;

    public WebPushModule(ILogger<WebPushModule> log, string storageDir, string subject)
    {
        _log     = log;
        _subject = string.IsNullOrWhiteSpace(subject) ? "mailto:owner@a-r-i.ai" : subject;
        Directory.CreateDirectory(storageDir);
        _subsPath = Path.Combine(storageDir, "PushSubscriptions.json");

        VapidKeys keys = LoadOrCreateVapidKeys(Path.Combine(storageDir, "VapidKeys.json"));
        _vapid = new VapidDetails(_subject, keys.PublicKey, keys.PrivateKey);

        foreach (StoredSubscription s in LoadSubscriptions())
            _subs[s.Endpoint] = s;
        _log.LogInformation("[WebPush] ready — {Count} subscription(s) loaded.", _subs.Count);
    }

    public void AddSubscription(string endpoint, string p256dh, string auth)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        _subs[endpoint] = new StoredSubscription(endpoint, p256dh, auth);
        Save();
        _log.LogInformation("[WebPush] subscription registered ({Count} total).", _subs.Count);
    }

    public void RemoveSubscription(string endpoint)
    {
        if (_subs.TryRemove(endpoint, out _)) { Save(); _log.LogInformation("[WebPush] subscription removed ({Count} left).", _subs.Count); }
    }

    public async Task SendPushNotification(string text, string? url = null, string? title = null)
    {
        if (_subs.IsEmpty) { _log.LogInformation("[WebPush] no subscriptions — nothing to notify."); return; }

        string payload = JsonSerializer.Serialize(new
        {
            title = string.IsNullOrWhiteSpace(title) ? "Ari" : title,
            body  = text,
            url,
        });

        List<string> dead = new();
        foreach (StoredSubscription s in _subs.Values.ToArray())
        {
            try
            {
                await _client.SendNotificationAsync(new PushSubscription(s.Endpoint, s.P256dh, s.Auth), payload, _vapid);
            }
            catch (WebPushException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Gone
                                                            or System.Net.HttpStatusCode.NotFound)
            {
                // 404/410 — the browser dropped this subscription; prune it.
                dead.Add(s.Endpoint);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[WebPush] send failed for one subscription: {Msg}", ex.Message);
            }
        }

        if (dead.Count > 0)
        {
            foreach (string endpoint in dead) _subs.TryRemove(endpoint, out _);
            Save();
            _log.LogInformation("[WebPush] pruned {Count} expired subscription(s).", dead.Count);
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
        catch (Exception ex) { _log.LogWarning("[WebPush] could not read VAPID keys ({Msg}); regenerating.", ex.Message); }

        VapidDetails generated = VapidHelper.GenerateVapidKeys();
        VapidKeys keys = new(generated.PublicKey, generated.PrivateKey);
        try { File.WriteAllText(path, JsonSerializer.Serialize(keys, JsonOpts)); }
        catch (Exception ex) { _log.LogWarning("[WebPush] could not persist VAPID keys: {Msg}", ex.Message); }
        _log.LogInformation("[WebPush] generated a new VAPID keypair.");
        return keys;
    }

    private List<StoredSubscription> LoadSubscriptions()
    {
        try
        {
            if (File.Exists(_subsPath))
                return JsonSerializer.Deserialize<List<StoredSubscription>>(File.ReadAllText(_subsPath), JsonOpts) ?? new();
        }
        catch (Exception ex) { _log.LogWarning("[WebPush] could not read subscriptions ({Msg}).", ex.Message); }
        return new();
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try { File.WriteAllText(_subsPath, JsonSerializer.Serialize(_subs.Values.ToList(), JsonOpts)); }
            catch (Exception ex) { _log.LogWarning("[WebPush] could not persist subscriptions: {Msg}", ex.Message); }
        }
    }
}

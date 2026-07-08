self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', () => self.clients.claim());
// No caching — just enough for Chrome to recognise this as a PWA.
self.addEventListener('fetch', e => e.respondWith(fetch(e.request)));

// Web Push: show Ari's proactive message as a notification. Payload is JSON { title, body, url }.
self.addEventListener('push', e => {
    let data = {};
    try { data = e.data ? e.data.json() : {}; } catch { data = { body: e.data ? e.data.text() : '' }; }
    const title = data.title || 'Ari';
    const options = {
        body: data.body || '',
        icon: '/images/icon-dark.png',
        badge: '/images/icon-dark.png',
        data: { url: data.url || '/' },
        tag: 'ari-proactive',
        renotify: true,
    };
    e.waitUntil(self.registration.showNotification(title, options));
});

// Focus an existing tab (navigating it to the deep-link) or open a new one.
self.addEventListener('notificationclick', e => {
    e.notification.close();
    const url = (e.notification.data && e.notification.data.url) || '/';
    e.waitUntil((async () => {
        const clientList = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        for (const client of clientList) {
            if ('focus' in client) {
                if ('navigate' in client) { try { await client.navigate(url); } catch { /* cross-origin guard */ } }
                return client.focus();
            }
        }
        if (self.clients.openWindow) return self.clients.openWindow(url);
    })());
});

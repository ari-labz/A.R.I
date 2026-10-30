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
        tag: data.tag || 'ari-proactive',
        renotify: true,
    };
    e.waitUntil(self.registration.showNotification(title, options));
});

// Open the chat the notification is about. A window that is already running is told which chat to open and answers back; navigating
// it is only the fallback, because some platforms (iOS, installed web apps) refuse to navigate an open window and would just focus it.
self.addEventListener('notificationclick', e => {
    e.notification.close();
    const url = (e.notification.data && e.notification.data.url) || '/';
    const thread = new URL(url, self.location.origin).searchParams.get('thread');
    e.waitUntil((async () => {
        const clientList = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        for (const client of clientList) {
            if (!('focus' in client)) continue;
            let answered = false;
            if (thread) {
                answered = await new Promise(resolve => {
                    const channel = new MessageChannel();
                    const timer = setTimeout(() => resolve(false), 1200);
                    channel.port1.onmessage = () => { clearTimeout(timer); resolve(true); };
                    try { client.postMessage({ type: 'ari-open-thread', thread }, [channel.port2]); } catch { clearTimeout(timer); resolve(false); }
                });
            }
            if (!answered && 'navigate' in client) { try { await client.navigate(url); } catch { /* cross-origin guard */ } }
            return client.focus();
        }
        if (self.clients.openWindow) return self.clients.openWindow(url);
    })());
});

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', () => self.clients.claim());
// No caching — just enough for Chrome to recognise this as a PWA.
self.addEventListener('fetch', e => e.respondWith(fetch(e.request)));

// Lanyard's service worker: keeps the app installable and shows push notifications.
//
// No offline caching. Lanyard is Blazor *Server*: every page needs a live SignalR circuit, so a
// cached shell would render and then sit there unable to do anything. The fetch handler stays a
// no-op: browsers detect that and skip the worker for navigation requests, so it costs nothing.
self.addEventListener('fetch', () => { });

// Take over straight away, so a changed worker (new notification handling) applies on the next
// page load rather than after every Lanyard tab has been closed.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

// Every push shows a notification. iPhone takes notification permission away from an app whose
// pushes arrive without one, so there are no silent pushes. The payload comes from
// WebPushSender: { title, body, url, tag }.
self.addEventListener('push', event => {
    let data = {};

    try {
        data = event.data ? event.data.json() : {};
    } catch {
        data = { body: event.data ? event.data.text() : '' };
    }

    const options = {
        body: data.body || '',
        icon: '/icon-192.png',
        data: { url: data.url || '/' }
    };

    // Same tag replaces the older notification (e.g. two rota changes in a row) instead of stacking.
    if (data.tag) {
        options.tag = data.tag;
        options.renotify = true;
    }

    event.waitUntil(self.registration.showNotification(data.title || 'Lanyard', options));
});

// Tapping a notification opens the page it's about: in an already-open Lanyard window if there is
// one, otherwise a new one. Only same-origin paths are followed.
self.addEventListener('notificationclick', event => {
    event.notification.close();

    const target = new URL((event.notification.data && event.notification.data.url) || '/', self.location.origin);
    const url = target.origin === self.location.origin ? target.href : self.location.origin + '/';

    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });

        for (const client of windows) {
            if (new URL(client.url).origin !== self.location.origin) {
                continue;
            }

            await client.focus();

            if ('navigate' in client) {
                try {
                    await client.navigate(url);
                    return;
                } catch {
                    // Not controlled by this worker yet; fall through to a new window.
                }
            }
        }

        await self.clients.openWindow(url);
    })());
});

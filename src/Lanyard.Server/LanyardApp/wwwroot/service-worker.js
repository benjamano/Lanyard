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
// WebPushSender: { title, body, url, tag, chat }.
self.addEventListener('push', event => {
    let data = {};

    try {
        data = event.data ? event.data.json() : {};
    } catch {
        data = { body: event.data ? event.data.text() : '' };
    }

    event.waitUntil(show(data));
});

async function show(data) {
    const options = {
        body: data.body || '',
        icon: '/icon-192.png',
        badge: '/icon-monochrome-512.png',
        data: { url: data.url || '/' }
    };

    // Same tag replaces the older notification (e.g. two rota changes in a row) instead of stacking.
    if (data.tag) {
        options.tag = data.tag;
        options.renotify = true;
    }

    let title = data.title || 'Lanyard';

    if (data.chat && data.tag) {
        try {
            title = await chatThread(data.chat, data.tag, options);
        } catch {
            // Anything odd about the old notification or the avatar: the plain one still shows.
        }
    }

    await self.registration.showNotification(title, options);
}

// Chat reads like a messaging app: one notification per conversation, with the newest few messages
// under each other and the sender's initials as the picture. The earlier messages come from the
// notification already showing for the conversation; once it's dismissed, or the conversation is
// opened (lanyardChat.clearNotifications), the next message starts afresh.
const ChatLinesShown = 6;

async function chatThread(chat, tag, options) {
    const [previous] = await self.registration.getNotifications({ tag });
    const earlier = (previous && previous.data && previous.data.chat) || { lines: [], count: 0 };

    const lines = [...earlier.lines, { sender: chat.sender, text: chat.text }].slice(-ChatLinesShown);
    const count = earlier.count + 1;
    const hidden = lines.every(x => !x.text);

    options.data.chat = { lines, count };
    options.timestamp = Date.now();
    options.icon = await initialsIcon(chat.sender) || options.icon;

    // A direct message is titled with the person; a group or channel with its name, and each line
    // says who wrote it. Anyone who has turned message text off gets a count instead.
    if (hidden) {
        const what = count === 1 ? 'New message' : `${count} new messages`;
        options.body = chat.conversation ? `${what} · latest from ${chat.sender}` : what;
    } else {
        const shown = lines.map(x => (chat.conversation ? `${x.sender}: ` : '') + (x.text || 'New message'));
        const more = count - lines.length;

        options.body = (more > 0 ? [`+${more} earlier`, ...shown] : shown).join('\n');
    }

    return chat.conversation || chat.sender;
}

// A coloured circle with the sender's initials, the colour picked from their name so each person
// keeps theirs. Null where the browser can't draw off-screen; the app icon is used then.
const AvatarColours = ['#c50f1f', '#ca5010', '#986f0b', '#498205', '#038387', '#0078d4', '#5c2e91', '#c239b3'];

async function initialsIcon(name) {
    if (typeof OffscreenCanvas === 'undefined') {
        return null;
    }

    const initials = name.split(' ').filter(Boolean).slice(0, 2).map(x => x[0].toUpperCase()).join('') || '?';
    let hash = 0;

    for (const c of name) {
        hash = (hash * 31 + c.charCodeAt(0)) >>> 0;
    }

    const size = 192;
    const canvas = new OffscreenCanvas(size, size);
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = AvatarColours[hash % AvatarColours.length];
    ctx.beginPath();
    ctx.arc(size / 2, size / 2, size / 2, 0, Math.PI * 2);
    ctx.fill();

    ctx.fillStyle = '#ffffff';
    ctx.font = `600 ${size * 0.4}px system-ui, sans-serif`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(initials, size / 2, size / 2 + size * 0.03);

    const bytes = new Uint8Array(await (await canvas.convertToBlob({ type: 'image/png' })).arrayBuffer());
    let binary = '';

    for (const b of bytes) {
        binary += String.fromCharCode(b);
    }

    return `data:image/png;base64,${btoa(binary)}`;
}

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

// blazorStart.js
// Starts Blazor manually (blazor.web.js is loaded with autostart="false" in App.razor) so the
// circuit reconnects quickly when a phone comes back to Lanyard after being in the background.
//
// What goes wrong without this, on a phone that was sent to the home screen:
// - The socket dies silently while the app is in the background (no close frame ever arrives).
// - When the browser finally notices, it starts a reconnect attempt while still in the background,
//   on a network the phone has put to sleep. That attempt hangs: SignalR's negotiate request only
//   gives up after 100 seconds and opening a WebSocket has no time limit at all. The browser's
//   pooled HTTP connections died too, so the next negotiate request hangs the same way.
// - Blazor waits for the hung attempt to finish, so coming back to the app shows "Reconnecting"
//   for a minute or more even though the network is fine again.
//
// The fixes, all below:
// 1. Reconnects skip the negotiate request and go straight to a WebSocket, as long as the first
//    connection used WebSockets (anyone whose network blocks them keeps the normal fallback). A
//    WebSocket always opens a new connection, so it never lands on a dead pooled one.
// 2. A reconnect attempt that hasn't connected within a few seconds is given up, and one that
//    started while the app was in the background is given up as soon as it comes back. Giving up
//    closes the attempt's half-open WebSocket too: browsers let only one WebSocket per host be
//    "connecting" at a time, so a stuck one would hold every later attempt in a queue.
// 3. When the app comes back from the background and the old socket has gone quiet, it gets a
//    couple of seconds to show signs of life (pings the server sent while the phone was away
//    arrive straight away on a live socket) instead of waiting out the 15s server timeout. If
//    nothing arrives it is dropped and replaced, which is cheap: the circuit is still on the server.
(() => {
    const AttemptTimeoutMs = 4000;    // a reconnect that hasn't connected by now is stuck, not slow
    const KeepAliveMs = 5000;         // the circuit hub's KeepAliveInterval in Program.cs
    const ResumeGraceMs = 2500;       // how long a socket that went quiet in the background gets
    const MinHiddenMs = 2000;         // shorter trips away don't need the socket checked
    const FastRetryForMs = 30000;     // on screen: retry every second for 30s, then every 5s
    const HiddenSlowAfterMs = 600000; // in the background: every 10s, then every minute after 10 min

    // Blazor shows its "An unhandled error has occurred" bar for any failed connection attempt
    // except a failed negotiate. A failed reconnect is expected, so every error from one is tagged
    // as that kind.
    const quietError = error => {
        const tagged = error instanceof Error ? error : new Error(String(error));
        tagged.errorType = "FailedToNegotiateWithServerError";
        return tagged;
    };

    let connectionCount = 0;
    let useWebSocketsDirectly = false; // set once the first connection is known to use WebSockets
    let current = null;                // the newest HubConnection
    let lastReceivedAt = Date.now();
    const attempts = new Set();        // reconnect attempts in flight
    let outageStartedAt = Date.now();
    let fastRetriesFrom = Date.now();  // the drop, or coming back to the app, whichever is later
    let hiddenAt = document.visibilityState === "visible" ? null : Date.now();
    let retryNowOnce = false;          // the next retry after coming back runs immediately
    let quietCheckTimer = null;

    function instrument(hub, sockets) {
        connectionCount++;
        current = hub;
        lastReceivedAt = Date.now();

        // Note every message (pings included) so a resumed socket can be judged alive or dead.
        const receive = hub.connection.onreceive;
        hub.connection.onreceive = data => {
            if (current === hub) lastReceivedAt = Date.now();
            receive(data);
        };

        const start = hub.start.bind(hub);
        if (connectionCount === 1) {
            // The first connection is left alone (a slow phone network may need longer to load
            // the page). Only note whether it ended up on WebSockets.
            hub.start = async () => {
                await start();
                useWebSocketsDirectly = typeof hub.connection.transport?._webSocketConstructor === "function";
            };
            return;
        }

        // Reconnects get a deadline, so a hung one can't hold up the next try.
        hub.start = () => new Promise((resolve, reject) => {
            const attempt = { startedAt: Date.now(), startedHidden: document.visibilityState !== "visible" };
            let settled = false;
            const finish = error => {
                if (settled) return;
                settled = true;
                clearTimeout(timer);
                attempts.delete(attempt);
                error === undefined ? resolve() : reject(quietError(error));
            };
            attempt.abandon = reason => {
                if (settled) return;
                for (const socket of sockets) {
                    if (socket.readyState === WebSocket.CONNECTING) socket.close();
                }
                hub.stop().catch(() => { });
                finish(new Error(reason));
            };
            const timer = setTimeout(() => attempt.abandon("Reconnect attempt timed out"), AttemptTimeoutMs);
            attempts.add(attempt);
            start().then(() => finish(), error => finish(error ?? new Error("Reconnect attempt failed")));
        });
    }

    function retryInterval(previousAttempts) {
        const now = Date.now();
        if (previousAttempts === 0) {
            outageStartedAt = now;
            fastRetriesFrom = now;
            return 0;
        }
        if (document.visibilityState !== "visible") {
            // Nobody is looking. Blazor cuts this wait short the moment the app is visible again.
            return now - outageStartedAt < HiddenSlowAfterMs ? 10000 : 60000;
        }
        if (retryNowOnce) {
            retryNowOnce = false;
            return 0;
        }
        // Never gives up while on screen: kiosks have nobody to press Retry.
        return now - fastRetriesFrom < FastRetryForMs ? 1000 : 5000;
    }

    function abandonAttempts(shouldAbandon, reason) {
        for (const attempt of [...attempts]) {
            if (shouldAbandon(attempt)) {
                retryNowOnce = true;
                attempt.abandon(reason);
            }
        }
    }

    // Back from the background: the tab was hidden, or a frozen page was thawed.
    function cameBack(awayFor) {
        const now = Date.now();
        fastRetriesFrom = now;

        // An attempt that began in the background is almost certainly stuck on the network the
        // phone had put to sleep. Drop it and try again straight away.
        abandonAttempts(a => a.startedHidden, "Gave up a reconnect attempt that started in the background");

        // The socket says it's connected but may have died while the app was away.
        if (awayFor < MinHiddenMs || !current || current.state !== "Connected") return;
        if (now - lastReceivedAt < KeepAliveMs) return; // still hearing from the server
        const hub = current;
        const checkFrom = now;
        clearTimeout(quietCheckTimer);
        quietCheckTimer = setTimeout(() => {
            if (current === hub && hub.state === "Connected" && lastReceivedAt < checkFrom) {
                // Same path as SignalR's own server timeout: closes the socket and lets Blazor
                // start reconnecting at once.
                hub.connection.stop(new Error("No messages from the server after coming back to the app"));
            }
        }, ResumeGraceMs);
    }

    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState !== "visible") {
            hiddenAt ??= Date.now();
            return;
        }
        const awayFor = hiddenAt === null ? 0 : Date.now() - hiddenAt;
        hiddenAt = null;
        cameBack(awayFor);
    });
    // A thawed page, or one restored from the back/forward cache, was away for an unknown time.
    document.addEventListener("resume", () => cameBack(Number.MAX_SAFE_INTEGER));
    window.addEventListener("pageshow", e => { if (e.persisted) cameBack(Number.MAX_SAFE_INTEGER); });
    // The network came back or changed: an attempt started on the old one won't finish.
    window.addEventListener("online", () => {
        const now = Date.now();
        abandonAttempts(a => now - a.startedAt > 1000, "Gave up a reconnect attempt started before the network came back");
    });

    Blazor.start({
        circuit: {
            reconnectionOptions: {
                maxRetries: Number.MAX_SAFE_INTEGER, // retryInterval paces the retries instead
                retryIntervalMilliseconds: retryInterval
            },
            configureSignalR: builder => {
                // Runs once per connection, so each connection keeps track of its own sockets.
                const sockets = new Set();
                class TrackedWebSocket extends WebSocket {
                    constructor(...args) {
                        super(...args);
                        sockets.add(this);
                        this.addEventListener("close", () => sockets.delete(this));
                    }
                }
                // Merges into the "_blazor" URL Blazor already set. Transport 1 = WebSockets.
                builder.withUrl("_blazor", useWebSocketsDirectly
                    ? { skipNegotiation: true, transport: 1, WebSocket: TrackedWebSocket }
                    : { WebSocket: TrackedWebSocket });
                // Must stay comfortably above the circuit hub's KeepAliveInterval (5s).
                builder.withServerTimeout(15000);
                const build = builder.build.bind(builder);
                builder.build = () => {
                    const hub = build();
                    instrument(hub, sockets);
                    return hub;
                };
            }
        }
    });
})();

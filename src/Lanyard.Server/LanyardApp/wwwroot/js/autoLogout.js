// autoLogout.js

window.autoLogout = (() => {
    let dotNetRef = null;
    let lastSentMs = 0;
    const EVENTS = ['mousemove', 'mousedown', 'keydown', 'touchstart', 'scroll', 'click'];

    // The server only needs to know activity happened recently, not every event. Without
    // this throttle a moving mouse alone fired 60-120 interop calls a second per user over
    // the Blazor circuit; the server-side check runs every 2 s and the timeout is minutes,
    // so one ping every few seconds is plenty.
    const MIN_INTERVAL_MS = 5000;

    function onActivity() {
        const now = Date.now();
        if (dotNetRef && now - lastSentMs >= MIN_INTERVAL_MS) {
            lastSentMs = now;
            dotNetRef.invokeMethodAsync('OnUserActivity');
        }
    }

    return {
        init(ref) {
            dotNetRef = ref;
            lastSentMs = 0;
            EVENTS.forEach(e => window.addEventListener(e, onActivity, { passive: true }));
        },
        dispose() {
            dotNetRef = null;
            EVENTS.forEach(e => window.removeEventListener(e, onActivity));
        }
    };
})();
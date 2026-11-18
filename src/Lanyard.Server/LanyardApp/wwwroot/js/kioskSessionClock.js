// kioskSessionClock.js
// Ticks the Clients page's Session Length column once a second in the browser, so the server
// doesn't re-render the whole grid every second just to move a clock. Each clock is a
// KioskSessionClock span carrying the session's length in whole seconds when the server rendered
// it (data-elapsed-seconds). Counting on from that, rather than from a start time, keeps the
// clock right even when this PC's clock disagrees with the server's. Same format as
// KioskSessionFormat.Format.

let timer = null;
let root = null;

// The rendered value each clock was last seen with, and when, so a server re-render (new
// data-elapsed-seconds) restarts the count from the fresh value.
const seen = new WeakMap();

function pad(value) {
    return String(value).padStart(2, '0');
}

function format(totalSeconds) {
    const seconds = Math.max(0, Math.floor(totalSeconds));
    const days = Math.floor(seconds / 86400);
    const clock = `${pad(Math.floor(seconds / 3600) % 24)}:${pad(Math.floor(seconds / 60) % 60)}:${pad(seconds % 60)}`;

    return days > 0 ? `${days}d ${clock}` : clock;
}

function tick() {
    if (!root || !root.isConnected) {
        stop();
        return;
    }

    const now = Date.now();

    for (const clock of root.querySelectorAll('[data-elapsed-seconds]')) {
        const rendered = Number(clock.dataset.elapsedSeconds);
        let state = seen.get(clock);

        if (!state || state.rendered !== rendered) {
            state = { rendered, at: now };
            seen.set(clock, state);
        }

        const text = format(rendered + (now - state.at) / 1000);

        // Changes Blazor's own text node rather than replacing it, so a later server render
        // still has a node to update.
        if (clock.firstChild && clock.firstChild.nodeType === Node.TEXT_NODE) {
            clock.firstChild.nodeValue = text;
        } else {
            clock.textContent = text;
        }
    }
}

export function start(element) {
    stop();
    root = element;
    tick();
    timer = setInterval(tick, 1000);
}

export function stop() {
    if (timer !== null) {
        clearInterval(timer);
        timer = null;
    }

    root = null;
}

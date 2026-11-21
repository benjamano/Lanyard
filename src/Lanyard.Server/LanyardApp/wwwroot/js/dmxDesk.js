// dmxDesk.js - small browser-side helpers for the /dmx desk page.

const lastClientKey = 'lanyard.dmx.lastClientId';

// Touch-first device (phone, tablet): the desk starts locked so a pocket can't change the lights.
export function prefersLock() {
    return window.matchMedia('(pointer: coarse)').matches;
}

export function getLastClientId() {
    try {
        return localStorage.getItem(lastClientKey);
    } catch {
        return null;
    }
}

export function setLastClientId(id) {
    try {
        if (id) {
            localStorage.setItem(lastClientKey, id);
        } else {
            localStorage.removeItem(lastClientKey);
        }
    } catch {
        // Private browsing or blocked storage: the kiosk just isn't remembered.
    }
}

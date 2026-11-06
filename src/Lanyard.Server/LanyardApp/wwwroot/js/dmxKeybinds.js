// dmxKeybinds.js
// Window-level key listener for triggering DMX scenes on the /dmx desk.
// .NET pushes the set of bound keys via setKeys so unbound keys never round-trip
// and preventDefault can be decided synchronously.
//
// Bindings are saved as KeyboardEvent.key, lower-cased ("a", "1", " "). What a key reports can
// change while it is held (press 1, then Shift: keyup says "!"), which used to leave a held flash
// scene running. So the key that went down is remembered against the physical key (e.code) and
// released by that, whatever keyup reports.

export const dmxKeybinds = (() => {
    let dotNetRef = null;
    let boundKeys = new Set();
    const heldByCode = new Map();

    function normalise(key) {
        return (key ?? '').toLowerCase();
    }

    // True when the keystroke is meant for a text field, so typing a scene name or a step
    // duration never fires a scene. composedPath() sees into Fluent's shadow DOM; only real
    // text-entry elements count, so focus on a Fluent button or slider no longer blocks keybinds.
    function isTyping(e) {
        for (const el of e.composedPath()) {
            if (!(el instanceof Element)) {
                continue;
            }

            if (el.isContentEditable) {
                return true;
            }

            const tag = el.tagName;

            if (tag === 'TEXTAREA' || tag === 'SELECT') {
                return true;
            }

            if (tag === 'INPUT') {
                const type = (el.getAttribute('type') || 'text').toLowerCase();
                return !['range', 'checkbox', 'radio', 'button', 'submit', 'color'].includes(type);
            }
        }

        return false;
    }

    // An open dialog (fixture manager, confirmations, the keybind capture itself) owns the keyboard.
    function dialogOpen() {
        return !!document.querySelector('fluent-dialog[open], dialog[open], [role="dialog"][aria-modal="true"]');
    }

    function call(method, key) {
        if (!dotNetRef) {
            return;
        }

        // The circuit may be reconnecting or gone; a rejected promise here is not worth a console error.
        dotNetRef.invokeMethodAsync(method, key).catch(() => { });
    }

    function onKeyDown(e) {
        if (e.repeat || !dotNetRef || e.ctrlKey || e.altKey || e.metaKey || isTyping(e) || dialogOpen()) {
            return;
        }

        const key = normalise(e.key);

        if (!boundKeys.has(key)) {
            return;
        }

        e.preventDefault();
        heldByCode.set(e.code || key, key);
        call('OnKeybindDown', key);
    }

    function onKeyUp(e) {
        const code = e.code || normalise(e.key);
        const key = heldByCode.get(code);

        if (key === undefined) {
            return;
        }

        heldByCode.delete(code);
        e.preventDefault();
        call('OnKeybindUp', key);
    }

    // Alt-tab, switching browser tab or locking the screen while holding a key never delivers
    // its keyup: let go of everything instead of leaving a flash scene on.
    function releaseAll() {
        for (const key of heldByCode.values()) {
            call('OnKeybindUp', key);
        }

        heldByCode.clear();
    }

    function onVisibilityChange() {
        if (document.visibilityState !== 'visible') {
            releaseAll();
        }
    }

    return {
        init(ref) {
            dotNetRef = ref;
            window.addEventListener('keydown', onKeyDown);
            window.addEventListener('keyup', onKeyUp);
            window.addEventListener('blur', releaseAll);
            document.addEventListener('visibilitychange', onVisibilityChange);
        },
        setKeys(keys) {
            boundKeys = new Set((keys ?? []).map(k => (k ?? '').toLowerCase()));
        },
        dispose() {
            releaseAll();
            dotNetRef = null;
            boundKeys = new Set();
            window.removeEventListener('keydown', onKeyDown);
            window.removeEventListener('keyup', onKeyUp);
            window.removeEventListener('blur', releaseAll);
            document.removeEventListener('visibilitychange', onVisibilityChange);
        }
    };
})();

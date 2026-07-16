// dmxKeybinds.js
// Window-level key listener for triggering DMX scenes on the /dmx desk.
// .NET pushes the set of bound keys via setKeys so unbound keys never round-trip
// and preventDefault can be decided synchronously.

window.dmxKeybinds = (() => {
    let dotNetRef = null;
    let boundKeys = new Set();

    // Normalise a KeyboardEvent.key so matching is case-insensitive for single
    // characters. Named keys (ArrowUp, Enter, " ") are compared lower-cased too.
    function normalise(key) {
        return (key ?? '').toLowerCase();
    }

    // True when focus sits in a text-editing control, so typing a scene name or a
    // step duration never fires a scene. Walks into shadow DOM because Fluent
    // inputs render their real <input> inside a web component.
    function isEditableFocus() {
        let el = document.activeElement;

        while (el?.shadowRoot?.activeElement) {
            el = el.shadowRoot.activeElement;
        }

        if (!el) {
            return false;
        }

        if (el.isContentEditable) {
            return true;
        }

        const tag = el.tagName;

        return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT'
            || tag.startsWith('FLUENT-');
    }

    function onKeyDown(e) {
        if (e.repeat || !dotNetRef || isEditableFocus()) {
            return;
        }

        const key = normalise(e.key);

        if (!boundKeys.has(key)) {
            return;
        }

        e.preventDefault();
        dotNetRef.invokeMethodAsync('OnKeybindDown', key);
    }

    function onKeyUp(e) {
        if (!dotNetRef) {
            return;
        }

        const key = normalise(e.key);

        if (!boundKeys.has(key)) {
            return;
        }

        e.preventDefault();
        dotNetRef.invokeMethodAsync('OnKeybindUp', key);
    }

    return {
        init(ref) {
            dotNetRef = ref;
            window.addEventListener('keydown', onKeyDown);
            window.addEventListener('keyup', onKeyUp);
        },
        setKeys(keys) {
            boundKeys = new Set((keys ?? []).map(k => (k ?? '').toLowerCase()));
        },
        dispose() {
            dotNetRef = null;
            boundKeys = new Set();
            window.removeEventListener('keydown', onKeyDown);
            window.removeEventListener('keyup', onKeyUp);
        }
    };
})();

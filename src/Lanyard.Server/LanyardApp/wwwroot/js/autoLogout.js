// autoLogout.js

window.autoLogout = (() => {
    let dotNetRef = null;
    const EVENTS = ['mousemove', 'mousedown', 'keydown', 'touchstart', 'scroll', 'click'];

    function onActivity() {
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnUserActivity');
        }
    }

    return {
        init(ref) {
            dotNetRef = ref;
            EVENTS.forEach(e => window.addEventListener(e, onActivity, { passive: true }));
        },
        dispose() {
            dotNetRef = null;
            EVENTS.forEach(e => window.removeEventListener(e, onActivity));
        }
    };
})();
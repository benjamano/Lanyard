// viewportWatcher.js

window.viewportWatcher = (() => {
    let dotNetRef = null;
    let breakpoint = 768;

    function reportState() {
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnViewportChanged', window.innerWidth < breakpoint);
        }
    }

    return {
        init(ref, breakpointPx) {
            dotNetRef = ref;
            breakpoint = breakpointPx;
            window.addEventListener('resize', reportState, { passive: true });
            reportState();
        },
        dispose() {
            window.removeEventListener('resize', reportState);
            dotNetRef = null;
        }
    };
})();

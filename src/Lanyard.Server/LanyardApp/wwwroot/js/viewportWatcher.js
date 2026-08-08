// viewportWatcher.js

window.viewportWatcher = (() => {
    let dotNetRef = null;
    let breakpoint = 768;
    let lastReported = null;

    function reportState() {
        if (dotNetRef) {
            const isNarrow = window.innerWidth < breakpoint;

            if (isNarrow !== lastReported) {
                lastReported = isNarrow;
                dotNetRef.invokeMethodAsync('OnViewportChanged', isNarrow);
            }
        }
    }

    return {
        init(ref, breakpointPx) {
            dotNetRef = ref;
            breakpoint = breakpointPx;
            lastReported = null;
            window.addEventListener('resize', reportState, { passive: true });
            reportState();
        },
        dispose() {
            window.removeEventListener('resize', reportState);
            dotNetRef = null;
        }
    };
})();

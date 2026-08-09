// trainingScrollGate.js
//
// Tracks whether the currently-visible content of the training course wizard
// has been scrolled to its bottom. On desktop the wizard has a fixed height
// and its content pane (.fluent-wizard-content) scrolls internally; on mobile
// the wizard height is "auto" instead, so the content pane never overflows
// and the whole page scrolls. Resolving which one is actually scrollable at
// check time (rather than assuming one or the other) covers both layouts.

window.trainingScrollGate = (() => {
    const BOTTOM_THRESHOLD_PX = 24;

    let hostEl = null;
    let dotNetRef = null;
    let innerScrollEl = null;

    function isInnerScrollable(el) {
        return !!el && (el.scrollHeight - el.clientHeight) > BOTTOM_THRESHOLD_PX;
    }

    function computeIsAtBottom() {
        if (isInnerScrollable(innerScrollEl)) {
            return innerScrollEl.scrollTop + innerScrollEl.clientHeight
                >= innerScrollEl.scrollHeight - BOTTOM_THRESHOLD_PX;
        }

        const doc = document.documentElement;
        const pageScrollable = doc.scrollHeight - window.innerHeight > BOTTOM_THRESHOLD_PX;

        if (!pageScrollable) {
            return true;
        }

        return window.scrollY + window.innerHeight >= doc.scrollHeight - BOTTOM_THRESHOLD_PX;
    }

    function report() {
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnScrollGateChanged', computeIsAtBottom());
        }
    }

    function onScrollOrResize() {
        report();
    }

    return {
        attach(host, ref) {
            hostEl = host;
            dotNetRef = ref;
            innerScrollEl = hostEl.querySelector('.fluent-wizard-content');

            if (innerScrollEl) {
                innerScrollEl.addEventListener('scroll', onScrollOrResize, { passive: true });
            }

            window.addEventListener('scroll', onScrollOrResize, { passive: true });
            window.addEventListener('resize', onScrollOrResize, { passive: true });
            report();
        },
        resetForStep() {
            if (isInnerScrollable(innerScrollEl)) {
                innerScrollEl.scrollTop = 0;
            } else {
                const doc = document.documentElement;

                if (doc.scrollHeight - window.innerHeight > BOTTOM_THRESHOLD_PX) {
                    window.scrollTo({ top: 0 });
                }
            }

            report();
        },
        dispose() {
            if (innerScrollEl) {
                innerScrollEl.removeEventListener('scroll', onScrollOrResize);
            }

            window.removeEventListener('scroll', onScrollOrResize);
            window.removeEventListener('resize', onScrollOrResize);
            hostEl = null;
            dotNetRef = null;
            innerScrollEl = null;
        }
    };
})();

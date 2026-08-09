// trainingScrollGate.js
//
// Tracks whether the currently-visible content of the training course wizard
// has been scrolled to its bottom. On desktop the wizard has a fixed height
// and its content pane (.fluent-wizard-content) scrolls internally; on mobile
// the wizard height is "auto" instead, so that pane never overflows itself -
// the app's own content region (a FluentLayoutItem ancestor with its own
// overflow-y: auto) is what actually scrolls there, since document.body has
// overflow: hidden and never scrolls on its own. Rather than hardcode both
// cases, this walks up from the wizard's content pane to whichever ancestor
// is actually the overflowing one.

window.trainingScrollGate = (() => {
    const BOTTOM_THRESHOLD_PX = 24;

    let hostEl = null;
    let dotNetRef = null;

    function isScrollable(el) {
        return !!el && (el.scrollHeight - el.clientHeight) > BOTTOM_THRESHOLD_PX;
    }

    function resolveScrollContainer() {
        const inner = hostEl ? hostEl.querySelector('.fluent-wizard-content') : null;

        if (isScrollable(inner)) {
            return inner;
        }

        let node = inner ? inner.parentElement : null;

        while (node && node !== document.body) {
            if (isScrollable(node)) {
                return node;
            }

            node = node.parentElement;
        }

        return null;
    }

    function computeIsAtBottom() {
        const el = resolveScrollContainer();

        if (!el) {
            return true;
        }

        return el.scrollTop + el.clientHeight >= el.scrollHeight - BOTTOM_THRESHOLD_PX;
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
            document.addEventListener('scroll', onScrollOrResize, { capture: true, passive: true });
            window.addEventListener('resize', onScrollOrResize, { passive: true });
            report();
        },
        resetForStep() {
            const el = resolveScrollContainer();

            if (el) {
                el.scrollTop = 0;
            }

            report();
        },
        dispose() {
            document.removeEventListener('scroll', onScrollOrResize, { capture: true });
            window.removeEventListener('resize', onScrollOrResize);
            hostEl = null;
            dotNetRef = null;
        }
    };
})();

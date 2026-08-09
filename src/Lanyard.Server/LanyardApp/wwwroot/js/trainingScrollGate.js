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
//
// Every report is tagged with the wizard step index it was measured for. When
// the wizard swaps in a new (shorter) section, the browser clamps scrollTop
// down to the new maximum and fires a scroll event that genuinely measures
// "at bottom" - without the step tag, the server would credit that reading to
// the step the user has not actually read yet. The .NET side discards any
// report whose step index doesn't match the step it currently believes is
// active, so a clamp event fired for the departing step can never unlock the
// arriving one.

window.trainingScrollGate = (() => {
    const BOTTOM_THRESHOLD_PX = 24;

    let hostEl = null;
    let dotNetRef = null;
    let currentStepIndex = 0;

    // Dedupe guard (mirrors viewportWatcher.js): only call into .NET when the
    // reported value actually changes. The scroll listener is document-level and
    // capture-phase, so it also fires for unrelated scrollable elements; without
    // this every one of those would cost a SignalR round-trip.
    let lastReportedStepIndex = null;
    let lastReportedIsAtBottom = null;

    let contentResizeObserver = null;
    let observedContentEl = null;
    let warnedMissingContent = false;

    function isScrollable(el) {
        return !!el && (el.scrollHeight - el.clientHeight) > BOTTOM_THRESHOLD_PX;
    }

    function resolveContentPane() {
        if (!hostEl) {
            return null;
        }

        const inner = hostEl.querySelector('.fluent-wizard-content');

        if (!inner && !warnedMissingContent) {
            warnedMissingContent = true;
            console.warn('[trainingScrollGate] .fluent-wizard-content not found - scroll gating disabled');
        }

        return inner;
    }

    function resolveScrollContainer() {
        const inner = resolveContentPane();

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

        // The walk stops at document.body, but on layouts where the document
        // itself is the scrolling element (html/body rather than an in-app
        // region) nothing above would have matched. Check it before concluding
        // "nothing scrollable" - that conclusion fails open and disables the gate.
        if (isScrollable(document.scrollingElement)) {
            return document.scrollingElement;
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
        if (!dotNetRef) {
            return;
        }

        const isAtBottom = computeIsAtBottom();

        if (currentStepIndex === lastReportedStepIndex && isAtBottom === lastReportedIsAtBottom) {
            return;
        }

        lastReportedStepIndex = currentStepIndex;
        lastReportedIsAtBottom = isAtBottom;

        dotNetRef.invokeMethodAsync('OnScrollGateChanged', currentStepIndex, isAtBottom);
    }

    function onScrollOrResize() {
        report();
    }

    // Content can grow after first layout - most realistically an <img> pasted into
    // a section's rich-text body that only settles the layout once it has loaded.
    // Measuring once at first render would see "nothing to scroll yet" and unlock
    // the section permanently, so re-measure whenever the content pane resizes.
    function observeContentPane() {
        if (typeof ResizeObserver === 'undefined') {
            return;
        }

        const contentEl = resolveContentPane();

        if (!contentEl || contentEl === observedContentEl) {
            return;
        }

        if (!contentResizeObserver) {
            contentResizeObserver = new ResizeObserver(() => report());
        }
        else {
            contentResizeObserver.disconnect();
        }

        observedContentEl = contentEl;
        contentResizeObserver.observe(contentEl);
    }

    function resetReportTracking() {
        lastReportedStepIndex = null;
        lastReportedIsAtBottom = null;
    }

    return {
        attach(host, ref, stepIndex) {
            hostEl = host;
            dotNetRef = ref;
            currentStepIndex = stepIndex ?? 0;
            warnedMissingContent = false;
            resetReportTracking();
            document.addEventListener('scroll', onScrollOrResize, { capture: true, passive: true });
            window.addEventListener('resize', onScrollOrResize, { passive: true });
            observeContentPane();
            report();
        },
        resetForStep(stepIndex) {
            currentStepIndex = stepIndex ?? currentStepIndex;

            // A step change always means the previous report state is meaningless,
            // even when the raw booleans happen to match - re-evaluate from scratch.
            resetReportTracking();

            const el = resolveScrollContainer();

            if (el) {
                el.scrollTop = 0;
            }

            observeContentPane();
            report();
        },
        dispose() {
            document.removeEventListener('scroll', onScrollOrResize, { capture: true });
            window.removeEventListener('resize', onScrollOrResize);

            if (contentResizeObserver) {
                contentResizeObserver.disconnect();
                contentResizeObserver = null;
            }

            observedContentEl = null;
            resetReportTracking();
            hostEl = null;
            dotNetRef = null;
        }
    };
})();

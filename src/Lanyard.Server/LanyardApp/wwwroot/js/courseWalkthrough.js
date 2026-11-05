// courseWalkthrough.js
//
// Positions the spotlight and the callout card for the course player walkthrough
// (CourseWalkthrough.razor). Blazor renders both elements; this only moves them.
//
// The spotlight is a fixed box laid over the highlighted element, and its huge
// box-shadow dims everything else. The card is placed below the element when it
// fits, above it when it doesn't, and inside the element's visible area as a last
// resort (a tall section body on a phone fills the whole screen).
//
// Positions are refreshed every animation frame while the walkthrough is open
// rather than on resize/scroll events: the wizard re-lays itself out when
// viewportWatcher moves the stepper to the top, and content scrolls inside an
// ancestor rather than the window, neither of which fires a window event. Styles
// are only written when a rect actually changes, so an idle frame costs one
// getBoundingClientRect per element.

export const courseWalkthrough = (() => {
    const PADDING_PX = 6;
    const GAP_PX = 12;
    const EDGE_PX = 12;

    let spotlightEl = null;
    let calloutEl = null;
    let targetSelector = null;
    let frameHandle = null;
    let lastKey = null;

    function findTarget() {
        if (!targetSelector) {
            return null;
        }

        const el = document.querySelector(targetSelector);

        if (!el) {
            return null;
        }

        const rect = el.getBoundingClientRect();
        return rect.width > 0 && rect.height > 0 ? el : null;
    }

    function place() {
        if (!spotlightEl || !calloutEl) {
            return;
        }

        const viewportWidth = window.innerWidth;
        const viewportHeight = window.innerHeight;
        const cardWidth = calloutEl.offsetWidth;
        const cardHeight = calloutEl.offsetHeight;
        const target = findTarget();

        if (!target) {
            const key = `centre|${viewportWidth}|${viewportHeight}|${cardWidth}|${cardHeight}`;

            if (key === lastKey) {
                return;
            }

            lastKey = key;

            // Nothing to point at: dim the whole page. Chromium doesn't paint a box-shadow on a
            // zero-size box, so the spotlight covers the viewport and dims with its background.
            spotlightEl.dataset.mode = 'none';
            Object.assign(spotlightEl.style, {
                top: '0px', left: '0px', width: `${viewportWidth}px`, height: `${viewportHeight}px`
            });
            Object.assign(calloutEl.style, {
                top: `${Math.max(EDGE_PX, (viewportHeight - cardHeight) / 2)}px`,
                left: `${Math.max(EDGE_PX, (viewportWidth - cardWidth) / 2)}px`
            });
            return;
        }

        const rect = target.getBoundingClientRect();

        // Clamp to the viewport so a section body taller than the screen gets a
        // spotlight edge you can actually see.
        const top = Math.max(rect.top - PADDING_PX, EDGE_PX / 2);
        const left = Math.max(rect.left - PADDING_PX, EDGE_PX / 2);
        const bottom = Math.min(rect.bottom + PADDING_PX, viewportHeight - EDGE_PX / 2);
        const right = Math.min(rect.right + PADDING_PX, viewportWidth - EDGE_PX / 2);

        const key = `${top}|${left}|${bottom}|${right}|${viewportWidth}|${viewportHeight}|${cardWidth}|${cardHeight}`;

        if (key === lastKey) {
            return;
        }

        lastKey = key;

        spotlightEl.dataset.mode = 'target';
        Object.assign(spotlightEl.style, {
            top: `${top}px`, left: `${left}px`, width: `${Math.max(0, right - left)}px`, height: `${Math.max(0, bottom - top)}px`
        });

        let cardTop;

        if (bottom + GAP_PX + cardHeight <= viewportHeight - EDGE_PX) {
            cardTop = bottom + GAP_PX;
        } else if (top - GAP_PX - cardHeight >= EDGE_PX) {
            cardTop = top - GAP_PX - cardHeight;
        } else {
            cardTop = Math.max(EDGE_PX, bottom - cardHeight - GAP_PX);
        }

        const centredLeft = left + (right - left) / 2 - cardWidth / 2;
        const cardLeft = Math.min(Math.max(EDGE_PX, centredLeft), viewportWidth - cardWidth - EDGE_PX);

        Object.assign(calloutEl.style, { top: `${cardTop}px`, left: `${Math.max(EDGE_PX, cardLeft)}px` });
    }

    function loop() {
        place();
        frameHandle = window.requestAnimationFrame(loop);
    }

    function show(spotlight, callout, selector) {
        spotlightEl = spotlight;
        calloutEl = callout;
        targetSelector = selector || null;
        lastKey = null;

        const target = findTarget();

        if (target) {
            target.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        }

        if (frameHandle === null) {
            loop();
        } else {
            place();
        }

        // Move keyboard focus to the card so Esc and Tab work straight away.
        const primary = callout.querySelector('[data-walkthrough-primary]');
        (primary || callout).focus({ preventScroll: true });
    }

    function hide() {
        if (frameHandle !== null) {
            window.cancelAnimationFrame(frameHandle);
            frameHandle = null;
        }

        spotlightEl = null;
        calloutEl = null;
        targetSelector = null;
        lastKey = null;
    }

    return { show, hide };
})();

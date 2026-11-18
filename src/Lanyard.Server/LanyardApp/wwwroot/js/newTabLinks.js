// Makes Target="LinkTarget.Blank" work on FluentAnchorButton / FluentLink.
//
// Fluent UI Blazor v5 replaces those components' navigation with Blazor.navigateTo() for any
// same-origin href, and ignores the target. So a button meant to open /api/... (a PDF) in a new
// tab instead navigated the current tab client-side, where the Blazor router has no such page and
// shows "Not Found" - the request never reached the server.
//
// Listening in the capture phase on the document runs before the component's own click/keydown
// handlers, so we can open the tab ourselves and stop theirs.
(function () {
    const selector = 'fluent-anchor-button[target="_blank"], fluent-link[target="_blank"]';

    function findLink(event) {
        const target = event.target;

        if (!(target instanceof Element)) {
            return null;
        }

        const link = target.closest(selector);

        if (!link || link.hasAttribute('disabled') || link.getAttribute('aria-disabled') === 'true') {
            return null;
        }

        return link.getAttribute('href') ? link : null;
    }

    function open(event, link) {
        event.preventDefault();
        event.stopImmediatePropagation();
        window.open(new URL(link.getAttribute('href'), document.baseURI).href, '_blank', 'noopener');
    }

    document.addEventListener('click', function (event) {
        // Only the primary button; middle-click goes through auxclick and the browser already
        // treats it as "open in new tab" via the component's proxy anchor.
        if (event.button !== 0) {
            return;
        }

        const link = findLink(event);

        if (link) {
            open(event, link);
        }
    }, true);

    document.addEventListener('keydown', function (event) {
        if (event.key !== 'Enter') {
            return;
        }

        const link = findLink(event);

        if (link) {
            open(event, link);
        }
    }, true);
})();

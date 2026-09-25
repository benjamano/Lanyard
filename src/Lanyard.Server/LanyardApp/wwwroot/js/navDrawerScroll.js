// navDrawerScroll.js

// On a phone the hamburger opens the nav as a Fluent drawer, which always starts scrolled to the
// top - so reaching a link near the current page (e.g. anything under Rota) meant scrolling down
// every time. When the drawer opens, bring the active link into the middle of it instead.
//
// FluentLayout's hamburger drawer is a <fluent-drawer hamburger> that fires a bubbling "toggle"
// event; its scroll container is the dialog inside its shadow root, which scrollIntoView reaches.
// Listening on document means it survives Blazor re-rendering the drawer, with nothing to wire up.
document.addEventListener('toggle', (e) => {
    const drawer = e.target;

    if (!(drawer instanceof Element) || !drawer.matches('fluent-drawer[hamburger]')) {
        return;
    }

    if ((e.detail?.newState ?? e.newState) !== 'open') {
        return;
    }

    // Wait a frame so the drawer has laid out before measuring where the link is.
    requestAnimationFrame(() => {
        const active = drawer.querySelector('a.active');

        // A zero height means it's inside a collapsed category - scrolling to it would do nothing useful.
        if (active && active.offsetHeight > 0) {
            active.scrollIntoView({ block: 'center', behavior: 'instant' });
        }
    });
}, true);

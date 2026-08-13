// bootCloak.js
// Hides the page behind #app-splash until Fluent UI v5's JS has finished computing
// its design tokens (v5 has no CSS-only theme, so there's no way to avoid a flash
// of unstyled content other than cloaking the paint). Runs synchronously in <head>,
// before Blazor or Fluent's own script has loaded - see App.razor for load order.
(() => {
    const root = document.documentElement;

    // Fluent stores the user's explicit choice here; prefers-color-scheme alone
    // would contradict a user who picked Light on a dark OS.
    const readStoredThemeSettings = () => {
        try { return JSON.parse(localStorage.getItem('fluentui-blazor:theme-settings') || '{}'); }
        catch { return {}; }
    };
    const stored = readStoredThemeSettings();
    const isDark = stored.mode === 'dark'
        || (stored.mode !== 'light' && window.matchMedia('(prefers-color-scheme: dark)').matches);
    root.setAttribute('data-app-theme', isDark ? 'dark' : 'light');

    // MainLayout reads this before calling IThemeService.SetThemeAsync(color, isExact):
    // that overload re-derives ThemeSettings from FluentUI's own "current effective
    // mode", which on a fresh load hasn't been hydrated from storage yet and silently
    // drops the user's saved mode back to system-preference. Passing the raw stored
    // mode through explicitly keeps the color-only update from clobbering it.
    window.lanyardGetStoredThemeMode = () => readStoredThemeSettings().mode ?? null;

    // Set from JS, never in markup: if scripting is unavailable the page is
    // never cloaked, so it can never stay blank.
    root.setAttribute('data-app-booting', '');

    let revealed = false;
    const reveal = () => {
        if (revealed) { return; }
        revealed = true;
        clearTimeout(failsafe);
        root.removeAttribute('data-app-booting');
    };

    const failsafe = setTimeout(reveal, 5000);
    window.lanyardRevealApp = reveal;

    // defineComponents() runs at beforeStart but token application at
    // afterStarted, so waiting on the custom element alone would reveal
    // mid-flash. As of Fluent UI v5 rc.5, tokens are written as inline CSS
    // custom properties on <html> (initializeThemeSettings(), part of
    // beforeStart) rather than via a <link id="default-fuib-css"> element
    // (that was rc.3-only and no longer exists - re-verify on Fluent upgrades).
    // --colorNeutralBackground1 is the token default-fuib.css uses for
    // body { background-color }, so its presence means tokens are live.
    const isReady = () => {
        if (customElements.get('fluent-button') === undefined) { return false; }
        const bg = getComputedStyle(document.documentElement).getPropertyValue('--colorNeutralBackground1');
        return bg.trim() !== '';
    };

    const poll = () => {
        if (revealed) { return; }
        if (isReady()) {
            requestAnimationFrame(() => requestAnimationFrame(reveal));
            return;
        }
        setTimeout(poll, 30);
    };

    poll();
})();

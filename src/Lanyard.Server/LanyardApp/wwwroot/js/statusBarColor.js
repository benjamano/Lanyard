// statusBarColor.js
// Android colours the installed app's status bar (and Chrome's toolbar) from
// <meta name="theme-color">, and re-reads it whenever it changes. The company's accent
// colour is only known once MainLayout/Login resolve branding over the circuit, so it's
// cached here and re-applied synchronously on the next load - otherwise every cold start
// would flash the default colour before switching. Must load after the meta tag in <head>.
(() => {
    const storageKey = 'lanyard:status-bar-color';
    const isHexColor = value => typeof value === 'string' && /^#[0-9a-f]{6}$/i.test(value);

    const apply = color => {
        const meta = document.querySelector('meta[name="theme-color"]');
        if (meta) {
            meta.setAttribute('content', color);
        }
    };

    try {
        const cached = localStorage.getItem(storageKey);
        if (isHexColor(cached)) {
            apply(cached);
        }
    } catch { }

    window.lanyardSetStatusBarColor = color => {
        if (!isHexColor(color)) {
            return;
        }

        apply(color);

        try { localStorage.setItem(storageKey, color); } catch { }
    };
})();

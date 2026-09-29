// lanyardInstall.js - the browser half of the "Add Lanyard to your Home Screen" prompt
// (InstallAppPrompt.razor). This file only reads and remembers things on the device; the decision
// of what to show lives in InstallPromptRules on the server, where it's unit-tested.
window.lanyardInstall = (() => {
    const SETTINGS_KEY = 'lanyard.installPrompt';
    const DEVICE_KEY = 'lanyard.deviceId';
    const PAGE_VIEWS_KEY = 'lanyard.pageViews';
    const SHOWN_KEY = 'lanyard.installPromptShown';

    // Chrome, Edge and Samsung Internet on Android fire this when the app is installable. Kept so
    // our Install button can open the browser's own install dialog later. Registered as soon as
    // this script loads, because the event can fire before Blazor has started.
    let deferredPrompt = null;

    window.addEventListener('beforeinstallprompt', event => {
        event.preventDefault();
        deferredPrompt = event;
    });

    window.addEventListener('appinstalled', () => {
        deferredPrompt = null;
    });

    // Storage can throw (private mode, blocked site data); every read and write copes with that.
    function readSettings() {
        try {
            return JSON.parse(localStorage.getItem(SETTINGS_KEY)) || {};
        } catch {
            return {};
        }
    }

    function writeSettings(settings) {
        try {
            localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings));
        } catch {
            // Not remembered; the prompt may come back sooner.
        }
    }

    function session(key, value) {
        try {
            if (value === undefined) {
                return sessionStorage.getItem(key);
            }

            sessionStorage.setItem(key, value);
        } catch {
            return null;
        }
    }

    function isStandalone() {
        return window.matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
    }

    // A random id for this browser, so the server can tell one installed device from another.
    function deviceId() {
        try {
            let id = localStorage.getItem(DEVICE_KEY);

            if (!id) {
                id = crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random().toString(16).slice(2);
                localStorage.setItem(DEVICE_KEY, id);
            }

            return id;
        } catch {
            return null;
        }
    }

    return {
        getDevice() {
            return {
                userAgent: navigator.userAgent,
                maxTouchPoints: navigator.maxTouchPoints || 0,
                isStandalone: isStandalone(),
                deviceId: deviceId()
            };
        },

        countPageView() {
            const views = (parseInt(session(PAGE_VIEWS_KEY) || '0', 10) || 0) + 1;
            session(PAGE_VIEWS_KEY, String(views));

            return views;
        },

        async getState() {
            const settings = readSettings();
            const push = window.lanyardPush ? await window.lanyardPush.getState() : { supported: false, permission: 'unsupported', endpoint: null };

            return {
                userAgent: navigator.userAgent,
                maxTouchPoints: navigator.maxTouchPoints || 0,
                isStandalone: isStandalone(),
                canPromptNatively: !!deferredPrompt,
                pushSupported: push.supported,
                notificationPermission: push.permission,
                hasPushSubscription: !!push.endpoint,
                pageViewsThisSession: parseInt(session(PAGE_VIEWS_KEY) || '0', 10) || 0,
                shownThisSession: session(SHOWN_KEY) === '1',
                installSnoozedUntilUtc: settings.installSnoozedUntilUtc || null,
                installNeverAsk: !!settings.installNeverAsk,
                notifySnoozedUntilUtc: settings.notifySnoozedUntilUtc || null,
                notifyNeverAsk: !!settings.notifyNeverAsk
            };
        },

        markShown() {
            session(SHOWN_KEY, '1');
        },

        // kind is 'install' or 'notify'.
        snooze(kind, days) {
            const settings = readSettings();
            settings[kind + 'SnoozedUntilUtc'] = new Date(Date.now() + days * 86400000).toISOString();
            writeSettings(settings);
        },

        neverAsk(kind) {
            const settings = readSettings();
            settings[kind + 'NeverAsk'] = true;
            writeSettings(settings);
        },

        // Opens the browser's own install dialog. Resolves 'accepted', 'dismissed' or 'unavailable'.
        async promptInstall() {
            if (!deferredPrompt) {
                return 'unavailable';
            }

            const prompt = deferredPrompt;
            deferredPrompt = null;
            prompt.prompt();

            const choice = await prompt.userChoice;

            return choice.outcome;
        }
    };
})();

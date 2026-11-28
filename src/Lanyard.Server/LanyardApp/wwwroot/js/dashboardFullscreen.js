// dashboardFullscreen.js
//
// Browser full screen for the live dashboard view (Pages/Staff/Dashboards/ViewDashboard.razor).
// The page asks for full screen on its own wrapper element, and is told through
// OnFullscreenChanged(bool) whenever full screen starts or ends - including when the viewer leaves
// it with Esc or the browser's own controls, so the page's button always matches.
//
// iPhones don't support full screen for anything but video, so isSupported() lets the page hide
// the button there instead of showing one that does nothing.

export function isSupported() {
    return !!document.fullscreenEnabled;
}

export function watch(element, dotNet) {
    const onChange = () => {
        dotNet.invokeMethodAsync('OnFullscreenChanged', document.fullscreenElement === element)
            .catch(() => { /* the page has gone */ });
    };

    document.addEventListener('fullscreenchange', onChange);

    return {
        dispose() {
            document.removeEventListener('fullscreenchange', onChange);
            if (document.fullscreenElement === element) {
                document.exitFullscreen().catch(() => { });
            }
        }
    };
}

export async function enter(element) {
    try {
        await element.requestFullscreen({ navigationUI: 'hide' });
        return true;
    } catch {
        return false;
    }
}

export async function exit() {
    if (document.fullscreenElement) {
        try {
            await document.exitFullscreen();
        } catch {
            // Already leaving full screen.
        }
    }
}

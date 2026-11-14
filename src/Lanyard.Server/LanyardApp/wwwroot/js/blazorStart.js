// blazorStart.js
// Starts Blazor manually (blazor.web.js is loaded with autostart="false" in App.razor) so the
// circuit's reconnection behaviour suits phones that background the tab.
//
// The framework default retries 10 times with no delay, then every 5s, then every 30s. A phone
// coming back from the background usually has no network for its first second or so, so those
// 10 "instant" retries all fail within ~20ms and the user then sits on "Rejoining the server"
// for 5-15s after the network is actually back. Retrying every second instead reconnects about
// a second after the network returns.
(() => {
    // Device pages must never be auto-paused (.NET 11 AutoPause, enabled in Program.cs): a kiosk's
    // WebView2 window reports "hidden" when minimised or covered, and pausing it would stop the
    // projection program until someone brings the window back. The auto-pause script defers while
    // any Web Lock is held, so these pages take one for their whole lifetime. They're opened
    // directly by the device and never navigate elsewhere, so checking the path once is enough.
    const path = location.pathname.toLowerCase().replace(/\/+$/, '');
    const isDevicePage = path.startsWith('/kiosk/') || path.startsWith('/video-publisher/') || path === '/rota/terminal';
    if (isDevicePage && navigator.locks) {
        navigator.locks.request('lanyard-device-page-no-autopause', () => new Promise(() => { }));
    }

    const RetryEverySecondFor = 30;  // ~30s of quick retries, covers network wake-up and a server restart
    const MaxRetries = 100;          // then every 5s for ~6 more minutes, past the 3 min circuit retention

    Blazor.start({
        circuit: {
            reconnectionOptions: {
                maxRetries: MaxRetries,
                retryIntervalMilliseconds: previousAttempts => {
                    if (previousAttempts >= MaxRetries) return null;
                    if (previousAttempts === 0) return 0;
                    return previousAttempts < RetryEverySecondFor ? 1000 : 5000;
                }
            },
            // A backgrounded phone's socket often dies silently (no close frame ever arrives), so the
            // page looks connected but ignores taps until this timeout notices. Must stay comfortably
            // above the circuit hub's KeepAliveInterval set in Program.cs (5s).
            configureSignalR: builder => builder.withServerTimeout(15000)
        }
    });
})();

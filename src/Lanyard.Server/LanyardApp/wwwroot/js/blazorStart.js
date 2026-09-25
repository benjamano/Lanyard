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

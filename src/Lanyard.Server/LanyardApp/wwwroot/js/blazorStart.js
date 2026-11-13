// blazorStart.js
// Starts Blazor manually (blazor.web.js is loaded with autostart="false" in App.razor) so the
// circuit's reconnection behaviour suits phones that background the tab.
//
// The framework default retries 10 times with no delay, then every 5s, then every 30s. A phone
// coming back from the background usually has no network for its first second or so, so those
// 10 "instant" retries all fail within ~20ms and the user then sits on "Rejoining the server"
// for 5-15s after the network is actually back. Retrying every second instead reconnects about
// a second after the network returns.
//
// It also sets up window.lanyardAuth - see below.
(() => {
    const RetryEverySecondFor = 30;  // ~30s of quick retries, covers network wake-up and a server restart
    const MaxRetries = 100;          // then every 5s for ~6 more minutes, past the 3 min circuit retention

    // The circuit's SignalR connection. Blazor builds a new one each time the circuit (re)connects,
    // so this is always the latest; Blazor itself doesn't expose it.
    let circuitConnection = null;

    // Picks up a change to what the user's sign-in grants (an admin added or removed one of their
    // roles) without reloading the page. SessionRefreshListener.razor calls it.
    //  1. api/auth/refresh-session re-issues the auth cookie from the user/role store - the circuit
    //     can't set cookies itself.
    //  2. refreshAuthentication() (.NET 11) sends that cookie to the circuit's hub, and Blazor hands
    //     the new principal to the circuit: AuthenticationStateChanged fires, AuthorizeView and
    //     the nav re-render, and server-side role checks use the new roles from then on.
    // If step 2 can't run (the circuit is mid-reconnect) nothing is lost: a reconnecting circuit
    // reads the user from the new cookie anyway.
    window.lanyardAuth = {
        async refresh() {
            const response = await fetch(new URL('api/auth/refresh-session', document.baseURI), {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json' },
                body: '{}'
            });

            if (response.status === 401) {
                // The session itself has ended (password reset, account disabled): reloading
                // takes them to the sign-in page.
                location.reload();
                return false;
            }

            if (!response.ok || !circuitConnection) {
                return false;
            }

            try {
                await circuitConnection.refreshAuthentication();
                return true;
            } catch (e) {
                console.warn('Lanyard: the circuit could not refresh its sign-in yet; it will on reconnect.', e);
                return false;
            }
        }
    };

    function trackCircuitConnection(builder) {
        const build = builder.build.bind(builder);
        builder.build = () => {
            circuitConnection = build();
            return circuitConnection;
        };
    }

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
            configureSignalR: builder => {
                // A backgrounded phone's socket often dies silently (no close frame ever arrives), so the
                // page looks connected but ignores taps until this timeout notices. Must stay comfortably
                // above the circuit hub's KeepAliveInterval set in Program.cs (5s).
                builder.withServerTimeout(15000);
                trackCircuitConnection(builder);
            }
        }
    });
})();

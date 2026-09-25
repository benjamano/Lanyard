// lanyardPush.js - this browser's push subscription, for NotificationsCard, InstallAppPrompt and
// DeviceSync. The server side is IPushSubscriptionService; this file only talks to the browser.
window.lanyardPush = (() => {
    function supported() {
        return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
    }

    function keyToBytes(base64url) {
        const padded = (base64url + '='.repeat((4 - base64url.length % 4) % 4)).replace(/-/g, '+').replace(/_/g, '/');
        const raw = atob(padded);
        const bytes = new Uint8Array(raw.length);

        for (let i = 0; i < raw.length; i++) {
            bytes[i] = raw.charCodeAt(i);
        }

        return bytes;
    }

    // A subscription is tied to the server's public key; after a key change it can never work again.
    function matchesKey(subscription, publicKey) {
        const current = subscription.options && subscription.options.applicationServerKey;

        if (!current) {
            return true;
        }

        const a = new Uint8Array(current);
        const b = keyToBytes(publicKey);

        return a.length === b.length && a.every((value, i) => value === b[i]);
    }

    function toJson(subscription) {
        const json = subscription.toJSON();

        return { endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth };
    }

    async function registration() {
        const existing = await navigator.serviceWorker.getRegistration();

        return existing || navigator.serviceWorker.register('/service-worker.js');
    }

    async function currentSubscription() {
        if (!supported()) {
            return null;
        }

        const reg = await navigator.serviceWorker.getRegistration();

        return reg ? reg.pushManager.getSubscription() : null;
    }

    return {
        supported,

        async getState() {
            let subscription = null;

            try {
                subscription = await currentSubscription();
            } catch {
                // Treated as not subscribed.
            }

            return {
                supported: supported(),
                permission: 'Notification' in window ? Notification.permission : 'unsupported',
                endpoint: subscription ? subscription.endpoint : null
            };
        },

        // The permission request has to run inside the tap itself. Blazor's @onclick goes to the
        // server and back first, and iPhone treats a prompt raised after that as unprompted and
        // silently refuses it. So the button gets a plain listener, and .NET hears the result.
        bindEnableButton(element, dotNetRef, publicKey) {
            if (!element || element._lanyardPushBound) {
                return;
            }

            element._lanyardPushBound = true;

            element.addEventListener('click', async () => {
                try {
                    const permission = await Notification.requestPermission();

                    if (permission !== 'granted') {
                        await dotNetRef.invokeMethodAsync('OnPushResult', permission, null);
                        return;
                    }

                    const reg = await registration();
                    let subscription = await reg.pushManager.getSubscription();

                    if (subscription && !matchesKey(subscription, publicKey)) {
                        await subscription.unsubscribe();
                        subscription = null;
                    }

                    if (!subscription) {
                        subscription = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyToBytes(publicKey) });
                    }

                    await dotNetRef.invokeMethodAsync('OnPushResult', 'granted', toJson(subscription));
                } catch (error) {
                    console.warn('Turning on notifications failed', error);

                    try {
                        await dotNetRef.invokeMethodAsync('OnPushResult', 'error', null);
                    } catch {
                        // Component has gone.
                    }
                }
            });
        },

        // Run on every app load while signed in. Reports this browser's existing subscription so
        // the server can refresh it (or move it to whoever is signed in now). Never creates one:
        // a new subscription only ever comes from someone tapping "Turn on", so the next person on
        // a shared browser isn't signed up just because permission was granted before.
        // After a server key change the old subscription is replaced, since it would never work.
        async sync(publicKey) {
            try {
                if (!publicKey || !supported() || Notification.permission !== 'granted') {
                    return null;
                }

                let subscription = await currentSubscription();

                if (!subscription) {
                    return null;
                }

                if (!matchesKey(subscription, publicKey)) {
                    await subscription.unsubscribe();
                    const reg = await registration();
                    subscription = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyToBytes(publicKey) });
                }

                return toJson(subscription);
            } catch (error) {
                console.warn('Push sync failed', error);
                return null;
            }
        },

        // Turns push off in this browser. Returns the endpoint that was removed, for the server.
        async unsubscribe() {
            try {
                const subscription = await currentSubscription();

                if (!subscription) {
                    return null;
                }

                const endpoint = subscription.endpoint;
                await subscription.unsubscribe();

                return endpoint;
            } catch (error) {
                console.warn('Turning off notifications failed', error);
                return null;
            }
        }
    };
})();

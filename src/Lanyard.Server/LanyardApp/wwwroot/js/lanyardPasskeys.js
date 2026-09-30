// lanyardPasskeys.js - the browser half of "sign in with Face ID / fingerprint" (WebAuthn passkeys).
// The phone does the biometric check itself; all this file does is pass the server's options to
// navigator.credentials and hand what comes back to AuthController's passkey endpoints.
window.lanyardPasskeys = (() => {
    // PublicKeyCredential.parse*OptionsFromJSON arrived in Safari 18 / Chrome 129. Older browsers
    // still have WebAuthn, so fall back to converting the base64url fields by hand.
    function fromBase64Url(value) {
        const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
        const padded = base64 + '='.repeat((4 - (base64.length % 4)) % 4);
        const binary = atob(padded);
        const bytes = new Uint8Array(binary.length);

        for (let i = 0; i < binary.length; i++) {
            bytes[i] = binary.charCodeAt(i);
        }

        return bytes.buffer;
    }

    function toBase64Url(buffer) {
        if (!buffer) {
            return undefined;
        }

        const bytes = buffer instanceof ArrayBuffer ? new Uint8Array(buffer) : new Uint8Array(buffer.buffer ?? buffer);
        let binary = '';

        for (let i = 0; i < bytes.byteLength; i++) {
            binary += String.fromCharCode(bytes[i]);
        }

        return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    }

    function parseCreationOptions(json) {
        if (typeof PublicKeyCredential.parseCreationOptionsFromJSON === 'function') {
            return PublicKeyCredential.parseCreationOptionsFromJSON(json);
        }

        return {
            ...json,
            challenge: fromBase64Url(json.challenge),
            user: { ...json.user, id: fromBase64Url(json.user.id) },
            excludeCredentials: (json.excludeCredentials ?? []).map(x => ({ ...x, id: fromBase64Url(x.id) })),
        };
    }

    function parseRequestOptions(json) {
        if (typeof PublicKeyCredential.parseRequestOptionsFromJSON === 'function') {
            return PublicKeyCredential.parseRequestOptionsFromJSON(json);
        }

        return {
            ...json,
            challenge: fromBase64Url(json.challenge),
            allowCredentials: (json.allowCredentials ?? []).map(x => ({ ...x, id: fromBase64Url(x.id) })),
        };
    }

    // Serialised by hand rather than with JSON.stringify(credential): some password managers
    // replace PublicKeyCredential with an object whose toJSON throws "Illegal invocation"
    // (see "Mitigate PublicKeyCredential.toJSON error" in the ASP.NET passkey docs).
    function serialise(credential) {
        const response = credential.response;

        return JSON.stringify({
            authenticatorAttachment: credential.authenticatorAttachment ?? undefined,
            clientExtensionResults: credential.getClientExtensionResults?.() ?? {},
            id: credential.id,
            rawId: toBase64Url(credential.rawId),
            response: {
                attestationObject: toBase64Url(response.attestationObject),
                authenticatorData: toBase64Url(response.authenticatorData ?? response.getAuthenticatorData?.()),
                clientDataJSON: toBase64Url(response.clientDataJSON),
                publicKey: toBase64Url(response.getPublicKey?.()),
                publicKeyAlgorithm: response.getPublicKeyAlgorithm?.() ?? undefined,
                transports: response.getTransports?.() ?? undefined,
                signature: toBase64Url(response.signature),
                userHandle: toBase64Url(response.userHandle),
            },
            type: credential.type,
        });
    }

    // What to tell the person when the browser refuses. NotAllowedError is also what cancelling
    // the Face ID / fingerprint sheet raises, so it gets a gentle message rather than an error.
    function describe(error) {
        switch (error?.name) {
            case 'NotAllowedError':
            case 'AbortError':
                return { cancelled: true, message: 'Cancelled - nothing was changed.' };
            case 'InvalidStateError':
                return { cancelled: false, message: 'This device already has a passkey for your account.' };
            case 'SecurityError':
                return { cancelled: false, message: "Passkeys aren't available on this address. They need a secure (https) connection." };
            default:
                return { cancelled: false, message: error?.message || 'Something went wrong. Please try again.' };
        }
    }

    async function readMessage(response, fallback) {
        try {
            const body = await response.json();
            return body?.message || fallback;
        } catch {
            return fallback;
        }
    }

    return {
        isSupported() {
            return !!(window.PublicKeyCredential && navigator.credentials && window.isSecureContext);
        },

        // Whether this device has its own Face ID / Touch ID / fingerprint unlock. Only used to
        // word things - a phone without one can still use a passkey from another device.
        async hasPlatformAuthenticator() {
            try {
                return !!(await PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable?.());
            } catch {
                return false;
            }
        },

        // Adds a passkey to the signed-in account. Resolves to { ok, cancelled, message, name }.
        async register() {
            try {
                const optionsResponse = await fetch('/api/auth/passkey/creation-options', {
                    method: 'POST',
                    credentials: 'same-origin',
                });

                if (!optionsResponse.ok) {
                    return { ok: false, cancelled: false, message: await readMessage(optionsResponse, "Couldn't start adding a passkey.") };
                }

                const options = parseCreationOptions(await optionsResponse.json());
                const credential = await navigator.credentials.create({ publicKey: options });

                const saveResponse = await fetch('/api/auth/passkey/register', {
                    method: 'POST',
                    credentials: 'same-origin',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ credentialJson: serialise(credential) }),
                });

                if (!saveResponse.ok) {
                    return { ok: false, cancelled: false, message: await readMessage(saveResponse, "Couldn't save the passkey.") };
                }

                const saved = await saveResponse.json();
                return { ok: true, cancelled: false, message: 'Passkey added.', name: saved?.name ?? null };
            } catch (error) {
                return { ok: false, ...describe(error) };
            }
        },

        // Signs in from the login page: asks the phone for a passkey, then submits the login form
        // to the passkey endpoint so its location, "keep me signed in" and returnUrl fields go too.
        // Only resolves if it didn't navigate away, with { ok: false, cancelled, message }.
        async signIn(form) {
            try {
                const optionsResponse = await fetch('/api/auth/passkey/request-options', {
                    method: 'POST',
                    credentials: 'same-origin',
                });

                if (!optionsResponse.ok) {
                    return { ok: false, cancelled: false, message: "Couldn't start signing in. Please try again." };
                }

                const options = parseRequestOptions(await optionsResponse.json());
                const credential = await navigator.credentials.get({ publicKey: options });

                let field = form.querySelector('input[name="credentialJson"]');

                if (!field) {
                    field = document.createElement('input');
                    field.type = 'hidden';
                    field.name = 'credentialJson';
                    form.appendChild(field);
                }

                field.value = serialise(credential);
                form.action = '/api/auth/passkey-login-form';

                // submit(), not requestSubmit(): the username and password boxes are required for a
                // password sign-in, and would otherwise block this one.
                form.submit();

                return { ok: true, cancelled: false, message: null };
            } catch (error) {
                return { ok: false, ...describe(error) };
            }
        },
    };
})();

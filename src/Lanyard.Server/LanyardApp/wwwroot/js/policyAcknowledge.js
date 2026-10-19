// Records a policy acknowledgement over plain HTTP, so the server sees this browser's own request
// (IP address and user agent) rather than the Blazor circuit's. Called from ReadPolicy.razor.
window.lanyardPolicies = {
    acknowledge: async function (versionId) {
        try {
            const tokenResponse = await fetch('/api/policies/antiforgery', { credentials: 'same-origin', cache: 'no-store' });

            if (!tokenResponse.ok) {
                return { ok: false, error: 'Your session has expired. Please reload the page and try again.' };
            }

            const { headerName, token } = await tokenResponse.json();
            const response = await fetch(`/api/policies/versions/${encodeURIComponent(versionId)}/acknowledge`, {
                method: 'POST',
                credentials: 'same-origin',
                headers: { [headerName]: token }
            });

            const body = await response.json().catch(() => ({}));

            if (!response.ok) {
                return { ok: false, error: body.error || 'Your acknowledgement couldn\'t be recorded. Please try again.' };
            }

            return { ok: true, versionNumber: body.versionNumber, acknowledgedAtUtc: body.acknowledgedAtUtc, alreadyAcknowledged: body.alreadyAcknowledged };
        } catch {
            return { ok: false, error: 'You seem to be offline. Check your connection and try again.' };
        }
    }
};

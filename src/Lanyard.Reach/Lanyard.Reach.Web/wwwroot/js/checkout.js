// Stripe checkout for the customer ordering flow.
//
// The card details are entered inside Stripe's own iframe and never touch this page, this
// server, or the Lanyard server - which is what keeps card data out of scope entirely. All we
// ever hold is a client secret that authorises paying one specific order.

let stripe = null;
let elements = null;

export function mountPaymentElement(publishableKey, stripeAccountId, clientSecret, containerId) {
    // stripeAccount matters: the payment lives on the venue's own connected account, and
    // initialising without it would look for the intent on the platform account and fail.
    stripe = Stripe(publishableKey, { stripeAccount: stripeAccountId });

    elements = stripe.elements({
        clientSecret,
        appearance: {
            theme: 'stripe',
            variables: {
                // Pulled from the tenant's own custom properties so the payment step does not
                // suddenly look like a different company's website.
                colorPrimary: getComputedStyle(document.documentElement)
                    .getPropertyValue('--brand-primary').trim() || '#167a47',
                borderRadius: '10px',
                fontFamily: 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif'
            }
        }
    });

    const paymentElement = elements.create('payment', { layout: 'tabs' });
    paymentElement.mount(`#${containerId}`);
}

export async function confirmPayment() {
    if (!stripe || !elements) {
        return { ok: false, error: "The payment form isn't ready yet." };
    }

    // redirect: 'if_required' keeps the customer on the page for cards, while still allowing
    // the redirect that methods like some wallets and bank apps require.
    const result = await stripe.confirmPayment({ elements, redirect: 'if_required' });

    if (result.error) {
        return { ok: false, error: result.error.message ?? 'Your payment could not be completed.' };
    }

    const status = result.paymentIntent?.status;

    // "processing" is a success from the customer's point of view - the money is on its way and
    // the webhook will confirm it. Treating it as a failure would invite a second payment.
    return { ok: status === 'succeeded' || status === 'processing', error: null };
}

export function unmount() {
    stripe = null;
    elements = null;
}

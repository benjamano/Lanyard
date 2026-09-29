// contactForm.js
// Posts the homepage's contact form to /api/contact (ContactController). From the browser rather
// than the Blazor circuit so the endpoint's per-visitor rate limit sees the visitor's own address.
export async function send(enquiry) {
    let response;
    try {
        response = await fetch('/api/contact', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(enquiry),
            credentials: 'same-origin',
        });
    } catch {
        return { ok: false, message: "We couldn't reach Lanyard to send that. Please check your connection and try again." };
    }

    if (response.ok) {
        return { ok: true, message: null };
    }

    if (response.status === 429) {
        return { ok: false, message: "That's a few messages in a short time - please wait a little while and try again." };
    }

    const body = await response.json().catch(() => null);
    return { ok: false, message: (body && body.message) || "Your message couldn't be sent. Please try again." };
}

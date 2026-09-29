// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

// Brief blips (a phone waking up, a server restart) usually reconnect within a couple of the
// 1-second retries configured in blazorStart.js, so hold the modal back until the connection
// has been down for this long. If it reconnects first, the user never sees anything.
const ShowModalAfterMilliseconds = 3000;
let showModalTimer = null;

function showModalNow() {
    clearTimeout(showModalTimer);
    showModalTimer = null;
    if (!reconnectModal.open) {
        reconnectModal.showModal();
    }
}

function handleReconnectStateChanged(event) {
    const state = event.detail.state;

    // Terminal/attention states shouldn't wait out the delay.
    if (showModalTimer !== null && (state === "failed" || state === "paused" || state === "resume-failed")) {
        showModalNow();
    }

    if (state === "show") {
        if (!reconnectModal.open && showModalTimer === null) {
            showModalTimer = setTimeout(showModalNow, ShowModalAfterMilliseconds);
        }
    } else if (state === "hide") {
        clearTimeout(showModalTimer);
        showModalTimer = null;
        reconnectModal.close();
    } else if (state === "failed") {
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (state === "rejected") {
        location.reload();
    }
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // We'll reload the page so the user can continue using the app as quickly as possible.
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                location.reload();
            } else {
                reconnectModal.close();
            }
        }
    } catch (err) {
        // We got an exception, server is currently unavailable
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    }
}

async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        }
    } catch {
        location.reload();
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}
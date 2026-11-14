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

// Set while the server is restarting and we're resuming automatically; swaps the modal's text.
const ServerRestartingClassName = "lanyard-server-restarting";

// Resume attempts after a server-requested pause. With an overlapping deploy the new server is
// usually already up, but a plain restart or cold start takes longer, so keep trying for about
// three minutes before falling back to the Resume button.
const FirstResumeDelayMilliseconds = 1500;
const QuickResumeAttempts = 20;      // every 2s for ~40s
const MaxResumeAttempts = 50;        // then every 5s for ~2.5 more minutes
const MaxRejectedResumes = 3;        // "false" can also mean the pause hadn't finished yet, so retry a few times
let autoResumeGeneration = 0;

function showModalNow() {
    clearTimeout(showModalTimer);
    showModalTimer = null;
    if (!reconnectModal.open) {
        reconnectModal.showModal();
    }
}

function handleReconnectStateChanged(event) {
    const state = event.detail.state;

    // The server asked us to pause because it's restarting (a deploy). Our state is saved in the
    // browser, so wait for the new server and resume onto it without the user doing anything.
    const serverRestarting = state === "paused" && event.detail.remote === true;

    // Terminal/attention states shouldn't wait out the delay.
    if (showModalTimer !== null && !serverRestarting && (state === "failed" || state === "paused" || state === "resume-failed")) {
        showModalNow();
    }

    if (serverRestarting) {
        reconnectModal.classList.add(ServerRestartingClassName);
        if (!reconnectModal.open && showModalTimer === null) {
            showModalTimer = setTimeout(showModalNow, ShowModalAfterMilliseconds);
        }
        resumeAfterServerRestart();
    } else if (state !== "paused") {
        reconnectModal.classList.remove(ServerRestartingClassName);
    }

    if (state === "show") {
        if (!reconnectModal.open && showModalTimer === null) {
            showModalTimer = setTimeout(showModalNow, ShowModalAfterMilliseconds);
        }
    } else if (state === "hide") {
        autoResumeGeneration++;
        clearTimeout(showModalTimer);
        showModalTimer = null;
        reconnectModal.close();
    } else if (state === "failed") {
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (state === "rejected") {
        location.reload();
    }
}

// A resumed circuit numbers its .NET object references from 1 again, so a callback from the old
// page's components can land on a different component in the new circuit. Fluent's layout is the
// visible case: when the resume replaces it, the old (now detached) layout measures 0px wide and
// tells ".NET" it went mobile, which reaches the NEW layout and hides the desktop nav. The old
// references are dead anyway once the circuit has paused, so drop them before resuming.
function forgetOldCircuitCallbacks() {
    for (const el of document.querySelectorAll(".fluent-layout")) {
        el._fluentDotNetHelper = null;
    }
    for (const el of document.querySelectorAll(".fluent-layout-hamburger")) {
        el._fluentHamburgerDotNetHelper = null;
    }
}

async function resumeCircuit() {
    forgetOldCircuitCallbacks();
    return await Blazor.resumeCircuit();
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
            const resumeSuccessful = await resumeCircuit();
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
        const successful = await resumeCircuit();
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

async function resumeAfterServerRestart() {
    const generation = ++autoResumeGeneration;
    const stillCurrent = () => generation === autoResumeGeneration;
    const wait = ms => new Promise(resolve => setTimeout(resolve, ms));

    await wait(FirstResumeDelayMilliseconds);

    let rejected = 0;
    for (let attempt = 0; attempt < MaxResumeAttempts && stillCurrent(); attempt++) {
        try {
            // true: resumed on the new server. false: reached a server but it didn't resume us,
            // either because the pause was still finishing or the saved state wasn't accepted.
            // A throw means no server answered yet.
            if (await resumeCircuit()) {
                if (stillCurrent()) {
                    finishAutoResume();
                }
                return;
            }
            if (++rejected >= MaxRejectedResumes) {
                location.reload();
                return;
            }
        } catch {
            // Server still restarting; try again shortly.
        }
        await wait(attempt < QuickResumeAttempts ? 2000 : 5000);
    }

    // Give up waiting silently: show the normal "paused" message with the Resume button.
    if (stillCurrent()) {
        reconnectModal.classList.remove(ServerRestartingClassName);
        showModalNow();
    }
}

function finishAutoResume() {
    autoResumeGeneration++;
    clearTimeout(showModalTimer);
    showModalTimer = null;
    reconnectModal.classList.remove(ServerRestartingClassName);
    if (reconnectModal.open) {
        reconnectModal.close();
    }
}

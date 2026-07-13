window.setVideoVolume = (id, volume) => {
    const video = document.getElementById(id);
    if (video) {
        video.volume = volume;
    }
};

window._liveCaptureStreams = window._liveCaptureStreams || {};

// Acquires a MediaStream for the capture device whose label best matches deviceLabel
// (exact match first, then contains; empty label = default camera). Throws on failure.
// Shared by the local kiosk capture (startLiveCapture) and the WebRTC publisher
// (video-stream.js).
window._acquireCaptureStream = async (deviceLabel, enableAudio, targetWidth, targetHeight) => {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        throw new Error("Camera API unavailable. The page must be served from a secure origin (https or localhost).");
    }

    // Acquire a default stream first so enumerateDevices() returns labels (labels are
    // hidden until the page has an active camera permission).
    const primingStream = await navigator.mediaDevices.getUserMedia({ video: true, audio: false });

    const devices = await navigator.mediaDevices.enumerateDevices();
    const label = (deviceLabel || "").trim().toLowerCase();

    let videoDeviceId = null;
    if (label) {
        const videoInputs = devices.filter(d => d.kind === "videoinput");
        const match = videoInputs.find(d => d.label.toLowerCase() === label)
            || videoInputs.find(d => d.label.toLowerCase().includes(label));
        if (match) {
            videoDeviceId = match.deviceId;
        }
    }

    let audioConstraint = false;
    if (enableAudio) {
        const audioInputs = devices.filter(d => d.kind === "audioinput");
        const audioMatch = label ? audioInputs.find(d => d.label.toLowerCase().includes(label)) : null;
        audioConstraint = audioMatch ? { deviceId: { exact: audioMatch.deviceId } } : true;
    }

    // The ideal capture size steers the browser's constraint matching toward the camera
    // mode closest to the display (e.g. 16:9 1080p over the 4:3 default), or the largest
    // available mode when the target outresolves the camera.
    const videoConstraints = {
        width: { ideal: targetWidth },
        height: { ideal: targetHeight },
        aspectRatio: { ideal: targetWidth / targetHeight }
    };

    if (videoDeviceId) {
        videoConstraints.deviceId = { exact: videoDeviceId };
    }

    // Always re-acquire: the priming stream used default constraints (typically 640x480).
    primingStream.getTracks().forEach(t => t.stop());

    return await navigator.mediaDevices.getUserMedia({
        video: videoConstraints,
        audio: audioConstraint
    });
};

window.startLiveCapture = async (videoElementId, deviceLabel, enableAudio) => {
    try {
        window.stopLiveCapture(videoElementId);

        const scale = window.devicePixelRatio || 1;
        const targetWidth = Math.round((window.screen.width || 1920) * scale);
        const targetHeight = Math.round((window.screen.height || 1080) * scale);

        const stream = await window._acquireCaptureStream(deviceLabel, enableAudio, targetWidth, targetHeight);

        const video = document.getElementById(videoElementId);
        if (!video) {
            stream.getTracks().forEach(t => t.stop());
            return "Video element not found.";
        }

        window._liveCaptureStreams[videoElementId] = stream;
        video.srcObject = stream;
        video.muted = !enableAudio;

        try {
            await video.play();
        } catch {
            // Autoplay with sound can be blocked; retry muted so at least video shows.
            video.muted = true;
            await video.play().catch(() => { });
        }

        return "";
    } catch (e) {
        return e && e.message ? e.message : "Failed to start video capture.";
    }
};

window.stopLiveCapture = (videoElementId) => {
    try {
        const stream = window._liveCaptureStreams[videoElementId];
        if (stream) {
            stream.getTracks().forEach(t => t.stop());
            delete window._liveCaptureStreams[videoElementId];
        }

        const video = document.getElementById(videoElementId);
        if (video) {
            video.srcObject = null;
        }
    } catch {
        // Cleanup must never throw.
    }
};
// WebRTC plumbing for cross-client video streaming.
// videoPublisher runs on the hidden publisher page of the video source machine;
// videoViewer runs on the kiosk that displays the remote stream. SDP/ICE signaling
// travels through the server's VideoStreamSignalingService via Blazor circuits.

window.videoPublisher = (() => {
    let dotNetRef = null;
    const sessions = new Map(); // sessionId -> { pc, deviceKey, pendingCandidates, remoteDescSet }
    const streams = new Map();  // deviceKey -> { stream, refCount } — one camera open shared by N viewers

    async function acquireShared(deviceKey, deviceName, enableAudio, idealWidth, idealHeight) {
        const existing = streams.get(deviceKey);
        if (existing) {
            existing.refCount++;
            return existing.stream;
        }

        const stream = await window._acquireCaptureStream(deviceName, enableAudio, idealWidth, idealHeight);
        streams.set(deviceKey, { stream: stream, refCount: 1 });
        return stream;
    }

    function releaseShared(deviceKey) {
        const entry = streams.get(deviceKey);
        if (!entry) return;
        entry.refCount--;
        if (entry.refCount <= 0) {
            entry.stream.getTracks().forEach(t => t.stop());
            streams.delete(deviceKey);
        }
    }

    function endSession(sessionId) {
        const session = sessions.get(sessionId);
        if (!session) return;
        sessions.delete(sessionId);
        try { session.pc.close(); } catch { }
        releaseShared(session.deviceKey);
    }

    return {
        init(ref) {
            dotNetRef = ref;
        },

        async startSession(sessionId, deviceName, enableAudio, idealWidth, idealHeight) {
            try {
                const deviceKey = `${(deviceName || "").toLowerCase()}|${!!enableAudio}`;
                const stream = await acquireShared(deviceKey, deviceName, enableAudio, idealWidth || 1920, idealHeight || 1080);

                const pc = new RTCPeerConnection({ iceServers: [] });
                sessions.set(sessionId, { pc: pc, deviceKey: deviceKey, pendingCandidates: [], remoteDescSet: false });

                stream.getTracks().forEach(t => pc.addTrack(t, stream));

                pc.onicecandidate = e => {
                    if (e.candidate && dotNetRef) {
                        dotNetRef.invokeMethodAsync('OnPublisherIceCandidate', sessionId, JSON.stringify(e.candidate));
                    }
                };

                pc.onconnectionstatechange = () => {
                    if (pc.connectionState === 'failed' && sessions.has(sessionId)) {
                        endSession(sessionId);
                        if (dotNetRef) {
                            dotNetRef.invokeMethodAsync('OnPublisherSessionError', sessionId, 'The WebRTC connection failed.');
                        }
                    }
                };

                const offer = await pc.createOffer();
                await pc.setLocalDescription(offer);

                if (dotNetRef) {
                    await dotNetRef.invokeMethodAsync('OnPublisherOffer', sessionId, offer.sdp);
                }
            } catch (e) {
                endSession(sessionId);
                if (dotNetRef) {
                    await dotNetRef.invokeMethodAsync('OnPublisherSessionError', sessionId,
                        e && e.message ? e.message : 'Failed to start video capture.');
                }
            }
        },

        async receiveAnswer(sessionId, sdpAnswer) {
            const session = sessions.get(sessionId);
            if (!session) return;

            await session.pc.setRemoteDescription({ type: 'answer', sdp: sdpAnswer });
            session.remoteDescSet = true;

            for (const candidate of session.pendingCandidates) {
                await session.pc.addIceCandidate(candidate).catch(() => { });
            }
            session.pendingCandidates = [];
        },

        async addIceCandidate(sessionId, candidateJson) {
            const session = sessions.get(sessionId);
            if (!session) return;

            const candidate = JSON.parse(candidateJson);

            // ICE candidates can trickle in before the answer sets the remote description.
            if (session.remoteDescSet) {
                await session.pc.addIceCandidate(candidate).catch(() => { });
            } else {
                session.pendingCandidates.push(candidate);
            }
        },

        endSession(sessionId) {
            endSession(sessionId);
        },

        disposeAll() {
            for (const sessionId of Array.from(sessions.keys())) {
                endSession(sessionId);
            }
        },

        activeSessionCount() {
            return sessions.size;
        }
    };
})();

window.videoViewer = (() => {
    const sessions = new Map();        // sessionId -> { pc, videoElementId, pendingCandidates, remoteDescSet }
    const earlyCandidates = new Map(); // sessionId -> [candidate] arriving before receiveOffer runs

    return {
        getTargetDimensions() {
            const scale = window.devicePixelRatio || 1;
            return [
                Math.round((window.screen.width || 1920) * scale),
                Math.round((window.screen.height || 1080) * scale)
            ];
        },

        async receiveOffer(videoElementId, sessionId, sdpOffer, enableAudio, dotNetRef) {
            const pc = new RTCPeerConnection({ iceServers: [] });
            const session = { pc: pc, videoElementId: videoElementId, pendingCandidates: [], remoteDescSet: false, lostNotified: false, disconnectTimer: null };
            sessions.set(sessionId, session);

            pc.onicecandidate = e => {
                if (e.candidate) {
                    dotNetRef.invokeMethodAsync('OnViewerIceCandidate', sessionId, JSON.stringify(e.candidate));
                }
            };

            // The publisher can die without its Blazor circuit disposing for minutes; the
            // peer connection state is the fast, authoritative death signal.
            const notifyLost = () => {
                if (session.lostNotified || !sessions.has(sessionId)) return;
                session.lostNotified = true;
                dotNetRef.invokeMethodAsync('OnViewerConnectionLost', sessionId);
            };

            pc.onconnectionstatechange = () => {
                if (pc.connectionState === 'failed') {
                    notifyLost();
                } else if (pc.connectionState === 'disconnected') {
                    // 'disconnected' can be transient; only treat as lost if it persists.
                    if (!session.disconnectTimer) {
                        session.disconnectTimer = setTimeout(() => {
                            session.disconnectTimer = null;
                            if (pc.connectionState === 'disconnected' || pc.connectionState === 'failed') {
                                notifyLost();
                            }
                        }, 5000);
                    }
                } else if (pc.connectionState === 'connected' && session.disconnectTimer) {
                    clearTimeout(session.disconnectTimer);
                    session.disconnectTimer = null;
                }
            };

            pc.ontrack = async e => {
                const video = document.getElementById(videoElementId);
                if (!video) return;

                video.srcObject = e.streams[0];
                video.muted = !enableAudio;

                try {
                    await video.play();
                } catch {
                    video.muted = true;
                    await video.play().catch(() => { });
                }
            };

            await pc.setRemoteDescription({ type: 'offer', sdp: sdpOffer });
            session.remoteDescSet = true;

            const early = earlyCandidates.get(sessionId) || [];
            earlyCandidates.delete(sessionId);
            for (const candidate of early) {
                await pc.addIceCandidate(candidate).catch(() => { });
            }

            const answer = await pc.createAnswer();
            await pc.setLocalDescription(answer);

            return answer.sdp;
        },

        async addIceCandidate(sessionId, candidateJson) {
            const candidate = JSON.parse(candidateJson);
            const session = sessions.get(sessionId);

            if (!session) {
                // Candidate raced ahead of the offer; buffer until receiveOffer runs.
                const buffered = earlyCandidates.get(sessionId) || [];
                buffered.push(candidate);
                earlyCandidates.set(sessionId, buffered);
                return;
            }

            if (session.remoteDescSet) {
                await session.pc.addIceCandidate(candidate).catch(() => { });
            } else {
                session.pendingCandidates.push(candidate);
            }
        },

        stop(sessionId, videoElementId) {
            try {
                const session = sessions.get(sessionId);
                if (session) {
                    sessions.delete(sessionId);
                    if (session.disconnectTimer) {
                        clearTimeout(session.disconnectTimer);
                    }
                    try { session.pc.close(); } catch { }
                }
                earlyCandidates.delete(sessionId);

                const video = document.getElementById(videoElementId);
                if (video) {
                    video.srcObject = null;
                }
            } catch {
                // Cleanup must never throw.
            }
        }
    };
})();

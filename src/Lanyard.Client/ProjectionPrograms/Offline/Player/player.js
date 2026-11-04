// Messages from OfflineProjectionRunner (via ProjectionWindow.PostMessage):
//   { type: "theme", dark: true }
//   { type: "text", text: "..." }
//   { type: "video", src: "https://lanyard-media.local/<id>.mp4", loop: false, volume: 60 }
//   { type: "idle" }    nothing on this screen can play offline
(function () {
    const text = document.getElementById("text");
    const video = document.getElementById("video");
    const idle = document.getElementById("idle");

    function show(element) {
        for (const el of [text, video, idle]) {
            el.hidden = el !== element;
        }

        if (element !== video) {
            video.pause();
            video.removeAttribute("src");
            video.load();
        }
    }

    function handle(message) {
        switch (message.type) {
            case "theme":
                document.body.className = message.dark ? "dark" : "light";
                break;

            case "text":
                text.textContent = message.text ?? "";
                show(text);
                break;

            case "video":
                // Same video on consecutive steps keeps playing rather than restarting, like the
                // online page.
                if (video.getAttribute("src") !== message.src) {
                    video.src = message.src;
                }
                video.loop = !!message.loop;
                video.volume = Math.min(Math.max((message.volume ?? 100) / 100, 0), 1);
                show(video);
                video.play().catch(() => { /* autoplay is allowed by the kiosk's browser flags */ });
                break;

            case "idle":
                show(idle);
                break;
        }
    }

    window.chrome?.webview?.addEventListener("message", event => handle(event.data));
})();

// Seasonal decorations for the staff app (Christmas snow, Halloween pumpkins).
// MainLayout imports this module and calls apply(season) whenever the resolved season changes;
// "none" tears everything down. Nothing here takes clicks (pointer-events: none) and nothing is
// announced to screen readers (aria-hidden). The stylesheet is only fetched the first time a
// season is actually shown, so the rest of the year costs one tiny module and nothing else.

const STYLESHEET_ID = "lanyard-season-css";
const ROOT_ID = "lanyard-season";

let current = "none";
let teardown = null;

const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

reducedMotion.addEventListener?.("change", () => {
    // Re-run the current season so animation starts or stops with the OS setting.
    const season = current;
    current = "none";
    apply(season);
});

function ensureStylesheet() {
    if (document.getElementById(STYLESHEET_ID)) {
        return;
    }

    const link = document.createElement("link");
    link.id = STYLESHEET_ID;
    link.rel = "stylesheet";
    link.href = new URL("../css/seasonal-themes.css", import.meta.url).href;
    document.head.appendChild(link);
}

function createRoot() {
    const root = document.createElement("div");
    root.id = ROOT_ID;
    root.setAttribute("aria-hidden", "true");
    document.body.appendChild(root);
    return root;
}

export function apply(season) {
    season = (season || "none").toLowerCase();

    if (season === current) {
        return;
    }

    teardown?.();
    teardown = null;
    document.getElementById(ROOT_ID)?.remove();
    current = season;

    if (season === "none") {
        delete document.body.dataset.season;
        return;
    }

    ensureStylesheet();
    document.body.dataset.season = season;

    const root = createRoot();
    const builder = builders[season];

    if (builder) {
        teardown = builder(root, !reducedMotion.matches) ?? null;
    }
}

// --- Christmas ---------------------------------------------------------------------------------

function buildChristmas(root, animate) {
    if (!animate) {
        // The snow caps on cards are pure CSS and stay; only the falling snow needs motion.
        return null;
    }

    const canvas = document.createElement("canvas");
    canvas.className = "season-snow";
    root.appendChild(canvas);

    const ctx = canvas.getContext("2d");
    let width = 0;
    let height = 0;
    let flakes = [];
    let frame = 0;
    let last = 0;

    function makeFlake(randomY) {
        const radius = 1 + Math.random() * 2.4;
        return {
            x: Math.random() * width,
            y: randomY ? Math.random() * height : -radius * 2,
            r: radius,
            // Bigger flakes fall faster, which is what gives the field a sense of depth.
            speed: 18 + radius * 14 + Math.random() * 10,
            drift: 6 + Math.random() * 14,
            phase: Math.random() * Math.PI * 2,
            alpha: 0.55 + Math.random() * 0.4,
        };
    }

    function resize() {
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        width = window.innerWidth;
        height = window.innerHeight;
        canvas.width = Math.round(width * dpr);
        canvas.height = Math.round(height * dpr);
        canvas.style.width = `${width}px`;
        canvas.style.height = `${height}px`;
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

        const target = width < 600 ? 30 : 60;
        while (flakes.length < target) flakes.push(makeFlake(true));
        flakes.length = target;
    }

    function draw(now) {
        frame = requestAnimationFrame(draw);

        // Clamp the step so a tab that was in the background doesn't teleport every flake.
        const dt = Math.min((now - (last || now)) / 1000, 0.05);
        last = now;

        const dark = document.body.dataset.theme === "dark";
        // White on a white card is invisible, so light mode gets a pale blue flake instead.
        const colour = dark ? "255, 255, 255" : "150, 185, 220";

        ctx.clearRect(0, 0, width, height);

        for (const flake of flakes) {
            flake.y += flake.speed * dt;
            flake.phase += dt;
            const x = flake.x + Math.sin(flake.phase) * flake.drift;

            if (flake.y - flake.r > height) {
                Object.assign(flake, makeFlake(false));
            }

            ctx.beginPath();
            ctx.fillStyle = `rgba(${colour}, ${flake.alpha})`;
            ctx.arc(x, flake.y, flake.r, 0, Math.PI * 2);
            ctx.fill();
        }
    }

    function start() {
        if (!frame) {
            last = 0;
            frame = requestAnimationFrame(draw);
        }
    }

    function stop() {
        cancelAnimationFrame(frame);
        frame = 0;
    }

    function onVisibility() {
        document.hidden ? stop() : start();
    }

    resize();
    window.addEventListener("resize", resize);
    document.addEventListener("visibilitychange", onVisibility);
    if (!document.hidden) start();

    return () => {
        stop();
        window.removeEventListener("resize", resize);
        document.removeEventListener("visibilitychange", onVisibility);
    };
}

const builders = {
    christmas: buildChristmas,
};

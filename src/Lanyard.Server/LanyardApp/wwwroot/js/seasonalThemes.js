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

// --- Halloween ---------------------------------------------------------------------------------

// Original artwork: a squat pumpkin with a carved face. The face is a separate group so CSS can
// make the candle inside flicker.
const PUMPKIN_SVG = `
<svg viewBox="0 0 120 100" xmlns="http://www.w3.org/2000/svg" focusable="false">
  <path d="M58 18 C57 10 60 4 67 2 L69 6 C64 8 63 12 64 18 Z" fill="#5b7a2e"/>
  <ellipse cx="34" cy="58" rx="28" ry="36" fill="#e46f12"/>
  <ellipse cx="86" cy="58" rx="28" ry="36" fill="#e46f12"/>
  <ellipse cx="60" cy="58" rx="30" ry="40" fill="#f2861d"/>
  <path d="M60 19 C52 40 52 78 60 97 M60 19 C68 40 68 78 60 97" stroke="#c95c0b" stroke-width="2" fill="none" opacity="0.6"/>
  <g class="season-pumpkin-face">
    <path d="M30 46 L44 34 L48 52 Z"/>
    <path d="M90 46 L76 34 L72 52 Z"/>
    <path d="M56 56 L60 48 L64 56 Z"/>
    <path d="M26 66 C40 86 80 86 94 66 L86 70 L80 64 L72 72 L64 66 L56 72 L48 66 L40 72 L34 64 Z"/>
  </g>
</svg>`;

const BAT_SVG = `
<svg viewBox="0 0 100 40" xmlns="http://www.w3.org/2000/svg" focusable="false">
  <path d="M50 14 C46 14 45 10 46 7 L48 10 L52 10 L54 7 C55 10 54 14 50 14 Z
           M47 14 C40 10 30 6 18 8 C24 12 24 18 20 22 C26 20 32 22 34 28 C38 22 44 20 47 22 Z
           M53 14 C60 10 70 6 82 8 C76 12 76 18 80 22 C74 20 68 22 66 28 C62 22 56 20 53 22 Z
           M47 14 L53 14 L53 24 C51 27 49 27 47 24 Z"/>
</svg>`;

function buildHalloween(root, animate) {
    const patch = document.createElement("div");
    patch.className = "season-pumpkins";
    patch.innerHTML =
        `<span class="season-pumpkin season-pumpkin-small">${PUMPKIN_SVG}</span>` +
        `<span class="season-pumpkin">${PUMPKIN_SVG}</span>`;
    root.appendChild(patch);

    if (!animate) {
        return null;
    }

    let timer = 0;

    function launchBat() {
        if (!document.hidden) {
            const bat = document.createElement("span");
            bat.className = "season-bat";
            bat.innerHTML = BAT_SVG;
            bat.style.top = `${60 + Math.random() * 140}px`;
            bat.style.animationDuration = `${7 + Math.random() * 4}s`;
            if (Math.random() < 0.5) bat.classList.add("season-bat-reverse");
            bat.addEventListener("animationend", () => bat.remove(), { once: true });
            root.appendChild(bat);
        }

        timer = window.setTimeout(launchBat, 20000 + Math.random() * 20000);
    }

    // The first bat comes quickly so a preview shows one; then every 20-40s.
    timer = window.setTimeout(launchBat, 2500);

    return () => window.clearTimeout(timer);
}

const builders = {
    christmas: buildChristmas,
    halloween: buildHalloween,
};

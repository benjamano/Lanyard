#!/usr/bin/env node
// Record a narrated walkthrough video: a browser session with a voice-over,
// burned-in subtitles, a visible cursor and element highlights, explaining a
// feature step by step. Output is an .mp4 (H.264 + AAC, soft subtitle track
// too) ready for publish-screenshots.sh's manifest.
//
// A walkthrough script is a .mjs file:
//
//   export const meta = { title: 'Publishing a rota', start: '/rota' };
//   export default [
//     { say: 'This is the rota page.' },
//     { say: 'Press Publish to send it to staff.',
//       do: async (page, h) => { await h.click(page.getByRole('button', { name: 'Publish' })); } },
//   ];
//
// Each step's narration is synthesised with Piper first, so the recording can
// hold every step on screen for exactly as long as its line takes to say.
//
// Usage:
//   node walkthrough.mjs --script <file.mjs> --out <file.mp4> [--viewport 1440x900]
//     [--base-url http://localhost:5096] [--no-login] [--headed] [--keep-temp]

import { chromium } from 'playwright';
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdtemp, readdir, rm, writeFile } from 'node:fs/promises';
import { homedir, tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const PIPER_HOME = join(homedir(), '.local/share/piper');
const PIPER_BIN = process.env.PIPER_BIN || join(PIPER_HOME, 'venv/bin/piper');
const PIPER_VOICE = process.env.PIPER_VOICE || join(PIPER_HOME, 'voices/en_GB-jenny_dioco-medium.onnx');
const LOGIN_USER = process.env.LANYARD_USER || 'admin';
const LOGIN_PASSWORD = process.env.LANYARD_PASSWORD || 'Dev-Admin-Pw1!';

// Gap after each line before the next step starts, so it doesn't feel rushed.
const STEP_GAP_MS = 700;
const END_HOLD_MS = 1500;

const USAGE = `Record a narrated walkthrough video (voice-over + subtitles + cursor).

Usage:
  node walkthrough.mjs --script <file.mjs> --out <file.mp4> [options]

Options:
  --script <file>     Walkthrough script (.mjs, see below). Required.
  --out <file>        Output .mp4. Required.
  --viewport <WxH>    1440x900 (desktop, default) or 390x844 (phone).
  --base-url <url>    App URL. Default: http://localhost:5096
  --no-login          Don't sign in first (e.g. for public pages).
  --headed            Show the browser window.
  --keep-temp         Keep the temp dir (raw .webm, per-step .wav, .srt).

Script format:
  export const meta = {
    title: 'Publishing a rota',   // shown on the opening title card
    start: '/rota',               // page to open (un-narrated) before step 1
  };
  export default [
    { say: 'Narration for this step.',
      do: async (page, h) => { ... },   // optional; runs while the line plays
    },
  ];

Helpers passed as h:
  h.click(locator)            highlight, glide the cursor to it, click
  h.fill(locator, text)       glide, click, type visibly
  h.highlight(locator)        draw a box round it for the rest of the step
  h.point(locator)            glide the cursor to it without clicking
  h.goto(path)                navigate (relative to --base-url)
  h.wait(ms)                  pause
  h.scroll(locator)           scroll it into view smoothly

Sign-in happens before recording (admin / Dev-Admin-Pw1!, override with
LANYARD_USER / LANYARD_PASSWORD, and LANYARD_COMPANY to pick a company
other than the first), so the video starts already signed in.
Voice: Piper, ${PIPER_VOICE} (override with PIPER_VOICE / PIPER_BIN;
install with ../walkthrough-setup.sh).
`;

function die(msg) {
  console.error(`error: ${msg}`);
  process.exit(1);
}

function parseArgs(argv) {
  const args = { viewport: '1440x900', baseUrl: 'http://localhost:5096', login: true, headed: false, keepTemp: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--script') args.script = argv[++i];
    else if (a === '--out') args.out = argv[++i];
    else if (a === '--viewport') args.viewport = argv[++i];
    else if (a === '--base-url') args.baseUrl = argv[++i].replace(/\/$/, '');
    else if (a === '--no-login') args.login = false;
    else if (a === '--headed') args.headed = true;
    else if (a === '--keep-temp') args.keepTemp = true;
    else if (a === '-h' || a === '--help') args.help = true;
    else die(`unknown argument: ${a}`);
  }
  return args;
}

function run(cmd, cmdArgs, input) {
  return new Promise((ok, fail) => {
    const p = spawn(cmd, cmdArgs, { stdio: ['pipe', 'pipe', 'pipe'] });
    let out = '', err = '';
    p.stdout.on('data', (d) => (out += d));
    p.stderr.on('data', (d) => (err += d));
    p.on('error', fail);
    p.on('close', (code) => (code === 0 ? ok(out) : fail(new Error(`${cmd} exited ${code}: ${err.slice(-2000)}`))));
    if (input !== undefined) p.stdin.end(input);
    else p.stdin.end();
  });
}

async function synthesise(text, wavPath) {
  await run(PIPER_BIN, ['--model', PIPER_VOICE, '--output_file', wavPath, '--sentence_silence', '0.25'], text);
  const secs = await run('ffprobe', ['-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', wavPath]);
  return Math.round(parseFloat(secs) * 1000);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function srtTime(ms) {
  const h = Math.floor(ms / 3600000), m = Math.floor(ms / 60000) % 60, s = Math.floor(ms / 1000) % 60;
  const pad = (n, w = 2) => String(n).padStart(w, '0');
  return `${pad(h)}:${pad(m)}:${pad(s)},${pad(ms % 1000, 3)}`;
}

// Injected into every page (survives full navigations): the fake cursor, the
// caption bar, the highlight box and the title card. The caption text is kept
// in sessionStorage so a navigation mid-step doesn't blank it.
function overlayInit() {
  const ready = () => {
    if (document.getElementById('__wt_cursor')) return;
    const css = document.createElement('style');
    css.textContent = `
      #__wt_cursor { position: fixed; z-index: 2147483647; pointer-events: none; width: 22px; height: 22px;
        margin: -3px 0 0 -3px; transition: transform .12s; left: -50px; top: -50px; }
      #__wt_cursor.down { transform: scale(.8); }
      #__wt_caption { position: fixed; z-index: 2147483646; pointer-events: none; left: 50%; bottom: 28px;
        transform: translateX(-50%); max-width: min(88vw, 960px); padding: 10px 18px; border-radius: 10px;
        background: rgba(12,12,16,.82); color: #fff; font: 500 clamp(15px, 2.1vw, 21px)/1.4 system-ui, 'Segoe UI', sans-serif;
        text-align: center; box-shadow: 0 4px 18px rgba(0,0,0,.35); opacity: 0; transition: opacity .2s; }
      #__wt_caption.on { opacity: 1; }
      @media (max-width: 600px) { #__wt_caption { bottom: 92px; } } /* clear the phone bottom nav */
      #__wt_box { position: fixed; z-index: 2147483645; pointer-events: none; border: 3px solid #ff9f1a; border-radius: 8px;
        box-shadow: 0 0 0 4px rgba(255,159,26,.25); transition: all .25s; opacity: 0; }
      #__wt_box.on { opacity: 1; }
      #__wt_title { position: fixed; inset: 0; z-index: 2147483647; display: flex; align-items: center; justify-content: center;
        flex-direction: column; gap: 10px; background: #14151a; color: #fff; font-family: system-ui, 'Segoe UI', sans-serif;
        text-align: center; padding: 24px; transition: opacity .5s; }
      #__wt_title h1 { margin: 0; font-size: clamp(26px, 4.5vw, 48px); font-weight: 650; }
      #__wt_title p { margin: 0; font-size: clamp(14px, 1.8vw, 20px); color: #b8bcc8; }`;
    document.documentElement.appendChild(css);
    const cursor = document.createElement('div');
    cursor.id = '__wt_cursor';
    cursor.innerHTML = '<svg viewBox="0 0 24 24" width="22" height="22"><path d="M3 2l7 19 2.6-7.4L20 11z" fill="#111" stroke="#fff" stroke-width="1.6" stroke-linejoin="round"/></svg>';
    const caption = document.createElement('div');
    caption.id = '__wt_caption';
    const box = document.createElement('div');
    box.id = '__wt_box';
    document.documentElement.append(box, caption, cursor);
    const pos = JSON.parse(sessionStorage.getItem('__wt_pos') || 'null');
    if (pos) { cursor.style.left = pos[0] + 'px'; cursor.style.top = pos[1] + 'px'; }
    const text = sessionStorage.getItem('__wt_caption');
    if (text) { caption.textContent = text; caption.classList.add('on'); }
    addEventListener('mousemove', (e) => {
      cursor.style.left = e.clientX + 'px';
      cursor.style.top = e.clientY + 'px';
      sessionStorage.setItem('__wt_pos', JSON.stringify([e.clientX, e.clientY]));
    }, true);
    addEventListener('mousedown', () => cursor.classList.add('down'), true);
    addEventListener('mouseup', () => cursor.classList.remove('down'), true);
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', ready);
  else ready();
}

async function inPage(page, fn, arg) {
  // A navigation can race an overlay update; the next step re-applies it.
  try { await page.evaluate(fn, arg); } catch { /* page navigating */ }
}

function helpers(page, baseUrl) {
  let mouse = { x: 0, y: 0 };
  const box = async (locator) => {
    await locator.first().scrollIntoViewIfNeeded().catch(() => {});
    const b = await locator.first().boundingBox();
    if (!b) throw new Error(`walkthrough: element not visible: ${locator}`);
    return b;
  };
  const glide = async (x, y) => {
    const dist = Math.hypot(x - mouse.x, y - mouse.y);
    await page.mouse.move(x, y, { steps: Math.max(12, Math.min(45, Math.round(dist / 18))) });
    mouse = { x, y };
  };
  const h = {
    async highlight(locator) {
      const b = await box(locator);
      await inPage(page, (r) => {
        const el = document.getElementById('__wt_box');
        if (!el) return;
        Object.assign(el.style, { left: r.x - 6 + 'px', top: r.y - 6 + 'px', width: r.width + 12 + 'px', height: r.height + 12 + 'px' });
        el.classList.add('on');
      }, b);
    },
    async point(locator) {
      const b = await box(locator);
      await glide(b.x + b.width / 2, b.y + b.height / 2);
      await sleep(250);
    },
    async click(locator) {
      await h.highlight(locator);
      await h.point(locator);
      await page.mouse.down();
      await sleep(90);
      await page.mouse.up();
      await sleep(400);
      // The click usually changes the page, so the box would be left
      // floating over whatever is there now.
      await inPage(page, () => document.getElementById('__wt_box')?.classList.remove('on'));
    },
    async fill(locator, text) {
      await h.click(locator);
      await locator.first().pressSequentially(text, { delay: 55 });
    },
    async scroll(locator) {
      await locator.first().evaluate((el) => el.scrollIntoView({ behavior: 'smooth', block: 'center' }));
      await sleep(700);
    },
    async goto(path) {
      await page.goto(path.startsWith('http') ? path : baseUrl + path);
      await page.waitForLoadState('networkidle').catch(() => {});
      await sleep(800);
    },
    wait: sleep,
  };
  return h;
}

async function signIn(page, baseUrl) {
  // Same two-step flow as the verify skill's Login section.
  // The company list loads over the circuit and can take a while on a cold
  // server; wait for whichever step shows up rather than a fixed sleep.
  await page.goto(`${baseUrl}/login`);
  const picker = page.locator('.company-picker-button').first();
  const username = page.getByRole('textbox', { name: /Username/ });
  await picker.or(username).first().waitFor({ timeout: 60000 });
  // A click on the prerendered picker before the circuit is interactive is
  // silently dropped, so keep clicking until the credentials form appears.
  const company = process.env.LANYARD_COMPANY;
  for (let attempt = 0; await picker.isVisible() && !(await username.isVisible()); attempt++) {
    if (attempt === 8) die('company picker never led to the login form');
    await (company ? page.locator('.company-picker-button', { hasText: company }) : picker).click();
    await username.waitFor({ timeout: 5000 }).catch(() => {});
  }
  await page.waitForTimeout(500);
  await username.fill(LOGIN_USER);
  await page.getByRole('textbox', { name: /Password/ }).fill(LOGIN_PASSWORD);
  const location = page.locator('fluent-dropdown#locationId');
  if (await location.count()) {
    await location.click();
    await page.waitForTimeout(500);
    await page.locator('fluent-option:visible').first().click();
  }
  await page.locator('.fluent-stack-horizontal.login-submit-row').click();
  await page.waitForTimeout(4000);
  if (page.url().includes('/login')) {
    die(`sign-in didn't get past ${page.url()} -- check credentials / 2FA (see the verify skill)`);
  }
}

// ---------------------------------------------------------------------------

const args = parseArgs(process.argv.slice(2));
if (args.help) { console.log(USAGE); process.exit(0); }
if (!args.script) die('--script is required (see --help)');
if (!args.out) die('--out is required (see --help)');
const vp = /^(\d+)x(\d+)$/.exec(args.viewport);
if (!vp) die(`--viewport must look like 1440x900, got: ${args.viewport}`);
const width = Number(vp[1]), height = Number(vp[2]);
if (!existsSync(PIPER_BIN) || !existsSync(PIPER_VOICE)) {
  die(`Piper not found (${PIPER_BIN} / ${PIPER_VOICE}). Run .claude/scripts/walkthrough-setup.sh once.`);
}

const scriptPath = resolve(args.script);
const outPath = resolve(args.out);
let mod;
try { mod = await import(pathToFileURL(scriptPath).href); } catch (err) { die(`could not load ${scriptPath}: ${err.message}`); }
const steps = mod.default;
const meta = mod.meta || {};
if (!Array.isArray(steps) || !steps.length) die(`${scriptPath} must default-export a non-empty array of steps`);
steps.forEach((s, i) => { if (!s.say) die(`step ${i + 1} has no "say"`); });

const tmp = await mkdtemp(join(tmpdir(), 'lanyard-walkthrough-'));
const cleanup = async () => { if (!args.keepTemp) await rm(tmp, { recursive: true, force: true }); };

// 1. Voice-over, one clip per step.
console.log(`synthesising ${steps.length} line(s)...`);
const clips = [];
if (meta.title) clips.title = await synthesise(meta.title + '.', join(tmp, 'title.wav'));
for (let i = 0; i < steps.length; i++) {
  clips.push(await synthesise(steps[i].say, join(tmp, `step-${i}.wav`)));
}

// 2. Sign in (not recorded), carry the session into the recording context.
const browser = await chromium.launch({ headless: !args.headed });
const contextOpts = { viewport: { width, height }, isMobile: width < 600, deviceScaleFactor: 1 };
let storageState;
if (args.login) {
  const ctx = await browser.newContext(contextOpts);
  await signIn(await ctx.newPage(), args.baseUrl);
  storageState = await ctx.storageState();
  await ctx.close();
}

// 3. Record.
const videoDir = join(tmp, 'video');
const context = await browser.newContext({ ...contextOpts, storageState, recordVideo: { dir: videoDir, size: { width, height } } });
await context.addInitScript(overlayInit);
const page = await context.newPage();
page.setDefaultTimeout(15000);
const t0 = Date.now();
const h = helpers(page, args.baseUrl);
const setCaption = (text) => inPage(page, (t) => {
  if (t) sessionStorage.setItem('__wt_caption', t); else sessionStorage.removeItem('__wt_caption');
  const el = document.getElementById('__wt_caption');
  if (!el) return;
  el.textContent = t || '';
  el.classList.toggle('on', !!t);
}, text);
const clearHighlight = () => inPage(page, () => document.getElementById('__wt_box')?.classList.remove('on'));

const timeline = []; // { at, dur, text, wav }
let trimMs;
try {
  await h.goto(meta.start || '/');
  if (meta.title) {
    await inPage(page, ({ title, subtitle }) => {
      const card = document.createElement('div');
      card.id = '__wt_title';
      card.innerHTML = '<h1></h1><p></p>';
      card.querySelector('h1').textContent = title;
      card.querySelector('p').textContent = subtitle;
      document.documentElement.appendChild(card);
    }, { title: meta.title, subtitle: meta.subtitle || 'Lanyard walkthrough' });
    await sleep(300);
    trimMs = Date.now() - t0;
    timeline.push({ at: trimMs, dur: clips.title, text: meta.title, wav: join(tmp, 'title.wav') });
    await sleep(clips.title + 600);
    await inPage(page, () => { const c = document.getElementById('__wt_title'); c.style.opacity = '0'; setTimeout(() => c.remove(), 600); });
    await sleep(700);
  } else {
    trimMs = Date.now() - t0;
  }

  for (let i = 0; i < steps.length; i++) {
    const step = steps[i];
    const at = Date.now() - t0;
    console.log(`step ${i + 1}/${steps.length}: ${step.say}`);
    await setCaption(step.say);
    timeline.push({ at, dur: clips[i], text: step.say, wav: join(tmp, `step-${i}.wav`) });
    await Promise.all([
      step.do ? step.do(page, h) : Promise.resolve(),
      sleep(clips[i] + STEP_GAP_MS),
    ]).catch((err) => {
      err.message = `step ${i + 1} ("${step.say}"): ${err.message}`;
      throw err;
    });
    await clearHighlight();
  }
  await setCaption('');
  await sleep(END_HOLD_MS);
} catch (err) {
  const shot = outPath.replace(/\.\w+$/, '') + '.failed.png';
  await page.screenshot({ path: shot }).catch(() => {});
  await context.close().catch(() => {});
  await browser.close();
  await cleanup();
  die(`walkthrough failed: ${err.stack || err.message}
screenshot of the page at failure: ${shot}`);
}
await context.close();
await browser.close();

const webm = (await readdir(videoDir)).find((f) => f.endsWith('.webm'));
if (!webm) { await cleanup(); die('no video was recorded'); }

// 4. Subtitles (soft track) + mux voice-over onto the trimmed video.
const srt = timeline.map((c, i) => `${i + 1}\n${srtTime(c.at - trimMs)} --> ${srtTime(c.at - trimMs + c.dur + STEP_GAP_MS - 100)}\n${c.text}\n`).join('\n');
await writeFile(join(tmp, 'subtitles.srt'), srt);

const ff = ['-y', '-v', 'error', '-ss', (trimMs / 1000).toFixed(3), '-i', join(videoDir, webm)];
timeline.forEach((c) => ff.push('-i', c.wav));
ff.push('-i', join(tmp, 'subtitles.srt'));
const filters = timeline.map((c, i) => `[${i + 1}:a]adelay=${c.at - trimMs}:all=1[a${i}]`);
filters.push(`${timeline.map((_, i) => `[a${i}]`).join('')}amix=inputs=${timeline.length}:normalize=0:duration=longest[aout]`);
ff.push('-filter_complex', filters.join(';'),
  '-map', '0:v', '-map', '[aout]', '-map', `${timeline.length + 1}:s`,
  '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '30', '-pix_fmt', 'yuv420p', '-r', '25',
  '-c:a', 'aac', '-b:a', '96k', '-c:s', 'mov_text', '-metadata:s:s:0', 'language=eng',
  '-movflags', '+faststart', outPath);
try {
  await run('ffmpeg', ff);
} catch (err) {
  await cleanup();
  die(`ffmpeg failed: ${err.message}`);
}

const secs = parseFloat(await run('ffprobe', ['-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', outPath]));
const size = parseInt(await run('stat', ['-c', '%s', outPath]), 10);
if (args.keepTemp) console.log(`temp files kept in ${tmp}`);
await cleanup();
console.log(`wrote ${outPath} (${secs.toFixed(1)}s, ${(size / 1048576).toFixed(1)} MiB)`);
if (size > 10 * 1048576) console.warn('warning: over 10 MiB -- trim steps or split the walkthrough before publishing');

#!/usr/bin/env node
// Record a short UI-flow video for a PR, outside the Playwright MCP (which has
// no recording tool/flag in this repo's config -- see CLAUDE.md).
//
// A "scenario" is a plain .mjs file that default-exports an async function
// receiving the Playwright `page`. It should navigate/act however the demo
// needs; this runner only owns the browser/context/video lifecycle.
//
// Usage:
//   node record-video.mjs --scenario <file.mjs> --out <file.webm> \
//     [--viewport 1440x900] [--headed]
//
// Output is a real .webm file -- feed it straight into
// .claude/scripts/publish-screenshots.sh's manifest (viewport "desktop"/
// "phone" the same way screenshots are).

import { chromium } from 'playwright';
import { mkdtemp, readdir, rename, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

function die(msg) {
  console.error(`error: ${msg}`);
  process.exit(1);
}

function parseArgs(argv) {
  const args = { viewport: '1440x900', headed: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--scenario') args.scenario = argv[++i];
    else if (a === '--out') args.out = argv[++i];
    else if (a === '--viewport') args.viewport = argv[++i];
    else if (a === '--headed') args.headed = true;
    else if (a === '-h' || a === '--help') { args.help = true; }
    else die(`unknown argument: ${a}`);
  }
  return args;
}

const USAGE = `Record a short UI-flow video outside the Playwright MCP.

Usage:
  node record-video.mjs --scenario <file.mjs> --out <file.webm> [--viewport 1440x900] [--headed]

Options:
  --scenario <file>  Path to a .mjs file default-exporting an async
                      function(page) that drives the flow (required).
  --out <file>        Where to write the finished .webm (required).
  --viewport <WxH>    Recording size, e.g. 1440x900 (desktop) or 390x844
                      (phone). Default: 1440x900.
  --headed            Run with a visible browser window (default: headless).

Scenario example (record-video-scenario.example.mjs):

  export default async function run(page) {
    await page.goto('http://localhost:5096/login');
    await page.waitForTimeout(2500);
    // ... drive the flow with normal Playwright page.* calls ...
  }

Keep scenarios short -- a few seconds to under a minute. Videos published via
publish-screenshots.sh render as a "Watch video" link (GitHub's own file
viewer), not an inline player -- raw.githubusercontent.com won't serve video
as a playable type. See CLAUDE.md's "UI screenshots go on the PR" section.
`;

const args = parseArgs(process.argv.slice(2));
if (args.help) { console.log(USAGE); process.exit(0); }
if (!args.scenario) die('--scenario is required (see --help)');
if (!args.out) die('--out is required (see --help)');

const viewportMatch = /^(\d+)x(\d+)$/.exec(args.viewport);
if (!viewportMatch) die(`--viewport must look like 1440x900, got: ${args.viewport}`);
const width = Number(viewportMatch[1]);
const height = Number(viewportMatch[2]);

const scenarioPath = resolve(args.scenario);
const outPath = resolve(args.out);

let scenarioModule;
try {
  scenarioModule = await import(pathToFileURL(scenarioPath).href);
} catch (err) {
  die(`could not load scenario ${scenarioPath}: ${err.message}`);
}
const run = scenarioModule.default;
if (typeof run !== 'function') {
  die(`${scenarioPath} must default-export an async function(page)`);
}

const videoDir = await mkdtemp(join(tmpdir(), 'lanyard-video-'));

// Uses Playwright's bundled Chromium (not the 'chrome' channel) -- that's
// what's actually installed here (~/.cache/ms-playwright/chromium-*), while
// the 'chrome' channel used by the MCP needs a separate
// `npx playwright install chrome` this sandbox hasn't necessarily run.
const browser = await chromium.launch({ headless: !args.headed });
const context = await browser.newContext({
  viewport: { width, height },
  recordVideo: { dir: videoDir, size: { width, height } },
});
const page = await context.newPage();

try {
  await run(page);
} catch (err) {
  await context.close();
  await browser.close();
  await rm(videoDir, { recursive: true, force: true });
  die(`scenario threw: ${err.stack || err.message}`);
}

// Closing the context is what actually finalizes/flushes the video file.
await context.close();
await browser.close();

const produced = (await readdir(videoDir)).filter((f) => f.endsWith('.webm'));
if (produced.length !== 1) {
  await rm(videoDir, { recursive: true, force: true });
  die(`expected exactly one .webm in ${videoDir}, found: ${produced.join(', ') || '(none)'}`);
}

await rename(join(videoDir, produced[0]), outPath);
await rm(videoDir, { recursive: true, force: true });

console.log(`wrote ${outPath}`);

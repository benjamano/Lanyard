// Regenerates the product screenshots on the public homepage (wwwroot/images/landing/*.webp) from
// the demo company, so they always show the current UI with made-up data.
//
//   1. Run the app with Demo__Enabled=true (see the verify skill), fresh from the nightly reset or
//      a new database, so the demo data is untouched.
//   2. node landing-screenshots.mjs --base-url http://localhost:5096
//
// Light mode, demo banner hidden. Needs ffmpeg with libwebp for the conversion.
import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const arg = name => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : undefined; };
const base = arg('--base-url') ?? 'http://localhost:5096';
const outDir = join(dirname(fileURLToPath(import.meta.url)), '../../../src/Lanyard.Server/LanyardApp/wwwroot/images/landing');
const work = mkdtempSync(join(tmpdir(), 'landing-shots-'));

const shots = [
  { name: 'rota', role: 'admin', path: '/manage/rota', width: 1440, height: 900, scale: 1 },
  {
    name: 'chat', role: 'admin', path: '/chat', width: 1440, height: 900, scale: 1,
    prepare: async page => { await page.getByText('Riverside team', { exact: true }).first().click(); await page.waitForTimeout(1500); },
  },
  { name: 'music', role: 'admin', path: '/music', width: 1440, height: 900, scale: 1 },
  { name: 'my-shifts', role: 'staff', path: '/rota', width: 390, height: 844, scale: 2 },
];

const browser = await chromium.launch();

for (const shot of shots) {
  const ctx = await browser.newContext({ viewport: { width: shot.width, height: shot.height }, deviceScaleFactor: shot.scale });
  await ctx.addInitScript(() => {
    localStorage.setItem('fluentui-blazor:theme-settings', JSON.stringify({ mode: 'light' }));
    localStorage.setItem('lanyard:demo-branding-prompt-dismissed', '1');
  });
  const page = await ctx.newPage();
  await page.goto(`${base}/api/auth/demo-login?role=${shot.role}`, { waitUntil: 'networkidle' });
  await page.goto(base + shot.path, { waitUntil: 'networkidle' });
  await page.addStyleTag({ content: '.demo-banner { display: none !important; }' });
  await page.waitForTimeout(2000);
  await shot.prepare?.(page);
  // Let any "loaded" toast go before the picture is taken.
  await page.waitForTimeout(5500);

  const png = join(work, `${shot.name}.png`);
  await page.screenshot({ path: png });
  execFileSync('ffmpeg', ['-v', 'error', '-y', '-i', png, '-c:v', 'libwebp', '-quality', '82', join(outDir, `${shot.name}.webp`)]);
  console.log(`${shot.name}.webp`);
  await ctx.close();
}

await browser.close();
rmSync(work, { recursive: true, force: true });

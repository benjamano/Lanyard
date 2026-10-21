// Chat fan-out benchmark: N viewers sit in one location channel while a sender posts M messages.
// Reports the SQL statements (grouped by TagWithCallSite call site) and the bytes Postgres sent per
// posted message, and send-to-render latency on every viewer, and checks every viewer got every
// message in order.
//
// Needs its own Postgres with pg_stat_statements loaded (never the shared lanyard-postgres):
//   docker run -d --name lanyard-chatbench-pg -e POSTGRES_USER=lanyard_dev -e POSTGRES_PASSWORD=lanyard_dev_password \
//     -e POSTGRES_DB=chatbench -p 127.0.0.1:5433:5432 postgres:17 -c shared_preload_libraries=pg_stat_statements -c max_connections=300
//   then migrate it, start the server against it once (the dev seeder runs), CREATE EXTENSION pg_stat_statements,
//   and add staff bench1..bench31 (password Dev-Admin-Pw1!) at location 1.
// Start the server fresh before each run: closed pages' circuits stay subscribed to chat for minutes.
//
// Usage: node .claude/scripts/chat-fanout-bench.mjs <baseUrl> <viewers> <messages> <label> <outJson>
// Env: BENCH_PG_CONTAINER (default lanyard-chatbench-pg), BENCH_PG_DB (default chatbench).
import { execFileSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';

// Playwright comes from the recorder's install (cd .claude/scripts/record-video && npm install).
const { chromium } = await import(new URL('./record-video/node_modules/playwright/index.mjs', import.meta.url).href);
const PG_CONTAINER = process.env.BENCH_PG_CONTAINER || 'lanyard-chatbench-pg';
const PG_DB = process.env.BENCH_PG_DB || 'chatbench';

const [base, viewersArg, messagesArg, label, outJson] = process.argv.slice(2);
const viewers = Number(viewersArg);
const messages = Number(messagesArg);
const GAP_MS = 3000;
const PASSWORD = 'Dev-Admin-Pw1!';

const psql = (sql) => execFileSync('docker', ['exec', PG_CONTAINER, 'psql', '-U', 'lanyard_dev', '-d', PG_DB, '-tAc', sql], { encoding: 'utf8' }).trim();
const txBytes = () => Number(execFileSync('docker', ['exec', PG_CONTAINER, 'cat', '/proc/net/dev'], { encoding: 'utf8' })
    .split('\n').find((l) => l.includes('eth0:')).split(':')[1].trim().split(/\s+/)[8]);

const browser = await chromium.launch();

async function signIn(n) {
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, baseURL: base });
    let res = await context.request.post('/api/auth/login', { data: { username: `bench${n}`, password: PASSWORD, locationId: 1 } });
    if (res.status() === 429) {
        // The login limit is 25 a minute from one address.
        await new Promise((r) => setTimeout(r, 62000));
        res = await context.request.post('/api/auth/login', { data: { username: `bench${n}`, password: PASSWORD, locationId: 1 } });
    }
    if (!res.ok()) throw new Error(`login bench${n}: ${res.status()} ${await res.text()}`);
    return context;
}

// The sender is bench31; every user has opened chat once so the channel exists with them in it.
const sender = await signIn(31);
const senderPage = await sender.newPage();
await senderPage.goto('/chat');
await senderPage.waitForTimeout(3000);
const channelId = psql(`select "Id" from "ChatConversations" where "Kind" = 2 and "LocationId" = 1`);
if (!channelId) throw new Error('no location channel');

const viewerPages = [];
for (let n = 1; n <= viewers; n++) {
    const context = await signIn(n);
    const page = await context.newPage();
    await page.goto('/chat');
    await page.waitForTimeout(800);
    await page.goto(`/chat/${channelId}`);
    viewerPages.push(page);
}

await senderPage.goto(`/chat/${channelId}`);
for (const page of [...viewerPages, senderPage]) {
    await page.waitForSelector('.ql-editor, .thread-messages', { timeout: 60000 });
}

// Each viewer records when each token first appears on its screen.
for (const page of viewerPages) {
    await page.evaluate(() => {
        window.__seen = {};
        const scan = () => {
            for (const el of document.querySelectorAll('.msg-body')) {
                const m = el.textContent.match(/TKN\d+-\d+/);
                if (m && !window.__seen[m[0]]) window.__seen[m[0]] = Date.now();
            }
        };
        new MutationObserver(scan).observe(document.body, { subtree: true, childList: true, characterData: true });
        scan();
    });
}

// One message first: the first post in a channel brings every member's row up to date, once.
await senderPage.locator('.ql-editor').click();
await senderPage.locator('.ql-editor').fill('Warm-up');
await senderPage.locator('.ql-editor').press('Enter');
await senderPage.waitForTimeout(6000);
const run = Date.now();
psql('select pg_stat_statements_reset()');
const txBefore = txBytes();

const sentAt = {};
const editor = senderPage.locator('.ql-editor');
for (let m = 1; m <= messages; m++) {
    const token = `TKN${run}-${m}`;
    await editor.click();
    await editor.fill(`Message ${token}`);
    sentAt[token] = Date.now();
    await editor.press('Enter');
    await senderPage.waitForTimeout(GAP_MS);
}

// Long enough for the last one to land everywhere and for any delayed mark-read to run.
await senderPage.waitForTimeout(6000);
const txAfter = txBytes();

let rows = psql(`select calls || '|' || rows || '|' || round(total_exec_time::numeric, 2) || '|' || replace(replace(query, E'\\n', ' '), '|', '/')
    from pg_stat_statements where dbid = (select oid from pg_database where datname = '${PG_DB}')`)
    .split('\n').filter(Boolean).map((line) => {
        const [calls, rowCount, ms, ...rest] = line.split('|');
        const query = rest.join('|');
        const tag = query.match(/file: .*?\/([\w.]+\.(?:cs|razor)):(\d+)/i);
        const kind = query.trim().split(/\s+/).find((w) => /^(SELECT|UPDATE|INSERT|DELETE|BEGIN|COMMIT)$/i.test(w)) ?? 'OTHER';
        const table = query.match(/(?:FROM|INTO|UPDATE)\s+(?:ONLY\s+)?(?:"public"\.)?"(\w+)"/i)?.[1] ?? '';
        const site = tag ? `${tag[1]}:${tag[2]}` : /^DISCARD/i.test(query.trim()) ? '(connection reset)' : `(untagged ${kind.toUpperCase()} ${table})`;
        // Chat's own work, as opposed to background services that poll regardless (notifications, kiosks).
        const chat = /Chat/.test(site) || /^\(untagged \w+ (Chat|AspNetUsers|UserLocationMemberships)/.test(site) || /^\(untagged (BEGIN|COMMIT)/.test(site);
        return { calls: Number(calls), rows: Number(rowCount), ms: Number(ms), site, chat, query: query.slice(0, 160) };
    })
    .filter((r) => !/pg_stat_statements|pg_database/.test(r.query));

const all = rows;
const resets = all.filter((r) => r.site === '(connection reset)').reduce((s, r) => s + r.calls, 0);
const background = all.filter((r) => !r.chat && r.site !== '(connection reset)').reduce((s, r) => s + r.calls, 0);
rows = rows.filter((r) => r.chat);
const bySite = {};
for (const r of rows) {
    bySite[r.site] ??= { calls: 0, rows: 0, ms: 0 };
    bySite[r.site].calls += r.calls;
    bySite[r.site].rows += r.rows;
    bySite[r.site].ms += r.ms;
}

const latencies = [];
let missing = 0;
let outOfOrder = 0;
for (const page of viewerPages) {
    const seen = await page.evaluate(() => window.__seen);
    for (const token of Object.keys(sentAt)) {
        if (seen[token]) latencies.push(seen[token] - sentAt[token]); else missing++;
    }
    const order = await page.evaluate((prefix) => [...document.querySelectorAll('.msg-body')]
        .map((e) => e.textContent.match(new RegExp(prefix + '(\\d+)'))?.[1]).filter(Boolean).map(Number), `TKN${run}-`);
    for (let i = 1; i < order.length; i++) if (order[i] < order[i - 1]) outOfOrder++;
}
latencies.sort((a, b) => a - b);
const pct = (p) => latencies.length ? latencies[Math.min(latencies.length - 1, Math.ceil(p * latencies.length) - 1)] : null;

const totalCalls = rows.reduce((s, r) => s + r.calls, 0);
const result = {
    label, viewers, messages,
    statementsPerMessage: +(totalCalls / messages).toFixed(1),
    connectionResetsPerMessage: +(resets / messages).toFixed(1),
    backgroundStatementsPerMessageExcluded: +(background / messages).toFixed(1),
    statementsPerMessagePerViewer: +(totalCalls / messages / Math.max(1, viewers)).toFixed(2),
    rowsPerMessage: +(rows.reduce((s, r) => s + r.rows, 0) / messages).toFixed(1),
    kbSentByPostgresPerMessage: +((txAfter - txBefore) / 1024 / messages).toFixed(1),
    dbMsPerMessage: +(rows.reduce((s, r) => s + r.ms, 0) / messages).toFixed(1),
    latencyMs: { p50: pct(0.5), p95: pct(0.95), max: latencies.at(-1) ?? null, samples: latencies.length },
    missing, outOfOrder,
    bySite: Object.fromEntries(Object.entries(bySite).sort((a, b) => b[1].calls - a[1].calls)
        .map(([k, v]) => [k, { callsPerMessage: +(v.calls / messages).toFixed(2), rowsPerMessage: +(v.rows / messages).toFixed(1) }]))
};

writeFileSync(outJson, JSON.stringify(result, null, 2));
console.log(JSON.stringify({ ...result, bySite: undefined }, null, 2));

// Leave each page properly, so Blazor ends its circuit now rather than keeping it (and its
// chat subscriptions) for minutes and skewing the next run.
for (const page of [...viewerPages, senderPage]) {
    await page.goto('about:blank').catch(() => {});
}
await new Promise((r) => setTimeout(r, 5000));
await browser.close();

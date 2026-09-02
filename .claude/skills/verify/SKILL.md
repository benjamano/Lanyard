---
name: verify
description: Build, launch, and drive the Lanyard server app to verify changes end-to-end (Blazor UI via Playwright MCP, Postgres via docker).
---

# Verifying LanyardApp changes

## Prerequisites
- Docker Postgres must be running: container `lanyard-postgres` (localhost:5432, db/user `lanyard_dev`, password `lanyard_dev_password`). Check: `docker ps`.
- Don't build `LanyardApp.sln` (or the `.slnx`) as a whole — on Linux it fails on a project that can't build there. Which one you hit depends on the sandbox: `NETSDK1147: workloads must be installed: maui-android` from `Lanyard.Reach` (targets `net10.0-android`), or `NETSDK1100: To build a project targeting Windows on this operating system, set EnableWindowsTargeting` from `Lanyard.Client`. Build only the server project directly:
  `dotnet build src/Lanyard.Server/LanyardApp/Lanyard.App.csproj`
- `dotnet test src/Lanyard.Tests/Lanyard.Tests.csproj` does work and is the check CLAUDE.md asks for before calling a task done.
- First time in a fresh sandbox, Playwright's browser isn't installed yet — `mcp__playwright__browser_navigate` fails with `Chromium distribution 'chrome' is not found at /opt/google/chrome/chrome`. Fix once with:
  `npx playwright install chrome` (takes 1-2 min; run it in the background and wait rather than polling with short sleeps).

## Migrations
- **Migrations do NOT auto-apply in Development** — `Program.cs` only calls `MigrateAsync()` when `IsDevelopment() == false`. After scaffolding a migration, apply it manually:
  `dotnet ef database update --project src/Lanyard.Infrastructure --no-build`
- Use the `postgres` MCP tools for seed/inspect queries; `docker exec ... psql -c "..."` mangles quoting through PowerShell.

## Launch
```
dotnet run --project src/Lanyard.Server/LanyardApp/Lanyard.App.csproj --launch-profile http --no-build
```
(run in background; app listens on http://localhost:5096; wait for the port with Test-NetConnection). Stop with TaskStop when done.

### Port 5096 already in use
Check before launching: `ss -ltnp | grep 5096`, then `ps -p <pid> -o pid,cmd`. If the cmd path points somewhere other than *this* worktree's `bin/` output (e.g. the main checkout at `.../src/src/Lanyard.Server/...`, or a different worktree), it's someone else's server — do not kill it without asking first, it may be an active session. Two options:
- Ask the user for permission to stop it (they may say yes — e.g. so a fresh instance with your latest build can take over the port).
- Or run yours on a different port. `--no-launch-profile` skips `launchSettings.json`'s `ASPNETCORE_ENVIRONMENT=Development`, which breaks `appsettings.Development.json` loading (connection string, etc.) — set it explicitly:
  `ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5197 dotnet run --project src/Lanyard.Server/LanyardApp/Lanyard.App.csproj --no-launch-profile --no-build`

## Screenshots — presenting UI back to the user

Screenshots must end up **on the pull request**, not only inline in the chat. An image attached inline lives solely in the transcript, and the transcript loses images — they disappear permanently. The PR is durable and reachable from any device.

Capture into `.playwright-mcp/`, which is already gitignored, so a stray PNG can never be committed:

1. `browser_resize` to the viewport you want, `mcp__playwright__browser_navigate` to the page, then `mcp__playwright__browser_take_screenshot` with `scale: "css"` and `filename: ".playwright-mcp/<name>.png"`.
   **The Playwright MCP refuses paths outside the repo** — `/tmp/...` fails with "outside allowed roots", and a bare `filename` drops the PNG in the *repo root*, where it is untracked and **not** ignored. `.playwright-mcp/` is the one path that is both allowed and ignored.
2. `Read` each PNG path — renders it inline so the user sees it immediately (a screenshot call alone is invisible to them). Still do this; it is the fast feedback loop.
3. Publish to the PR (see below). Then `rm -f .playwright-mcp/*.png`.

Per the global CLAUDE.md, UI work needs **both a phone (390x844) and a desktop (1440x900)** layout, covering each new screen, each visible state (empty, populated, error), and any new dialog — not a single token shot.

For anything below the fold (e.g. a footer link), `browser_resize` to a taller viewport first, or the screenshot only shows what was visible at default size.

### Publishing to the PR

`.claude/scripts/publish-screenshots.sh` uploads the PNGs to the orphan `screenshots` branch and writes a managed `## Screenshots` section into the PR description. It talks to the GitHub API only — no checkout, no index write, no stash — so it is safe to run from a worktree.

Write a manifest describing the shots (`caption` = what it shows; optional `size` overrides the default viewport label):

```json
[
  { "file": ".playwright-mcp/desktop-analytics.png", "viewport": "desktop", "caption": "Analytics page, populated" },
  { "file": ".playwright-mcp/phone-analytics.png",   "viewport": "phone",   "caption": "Analytics page, populated" }
]
```

**Sequencing — capture before opening the PR.** Storage is keyed on branch name, not PR number, so you can upload first and let the PR be born with its screenshots already in the body:

```bash
# Preferred: fold the markdown straight into the new PR
.claude/scripts/publish-screenshots.sh --manifest .playwright-mcp/manifest.json --emit-markdown >> /tmp/pr-body.md
gh pr create --base dev --body-file /tmp/pr-body.md

# Or, against a PR that already exists
.claude/scripts/publish-screenshots.sh --manifest .playwright-mcp/manifest.json --pr 110
```

Re-running on the same branch overwrites the same paths and rewrites the section in place, so the description always shows the current UI — no duplicated sections, and no stale images (URLs are cache-busted by commit SHA).

## Login (Playwright MCP)

The seeded dev account is `admin` / `Dev-Admin-Pw1!` — that's `DatabaseSeeder.DevelopmentAdminPassword`, used whenever `Seed:AdminPassword` isn't configured. It is usually the *only* row in `AspNetUsers`; don't expect `bmercer` or any other name to exist.

**Check 2FA before you start.** If the seeded admin has `TwoFactorEnabled = true`, login stops at `/login/verify-2fa` and you cannot get past it unattended — email 2FA needs a Resend key that dev doesn't have, and the authenticator code isn't derivable. Check first:

```sql
SELECT "UserName", "TwoFactorEnabled" FROM "AspNetUsers";
```

If it's on, **ask the user before changing it** — 2FA state is shared with anything else using this database, and toggling it mid-session can break another session's testing. If they agree, flip it off, do the run, and set it back to `true` when finished.

Login is **two steps** — company picker, then credentials:

```js
await page.goto('http://localhost:5096/login');
await page.waitForTimeout(2500);
await page.locator('.company-picker-button').first().click();   // step 1
await page.waitForTimeout(2500);
await page.getByRole('textbox', { name: /Username/ }).fill('admin');
await page.getByRole('textbox', { name: /Password/ }).fill('Dev-Admin-Pw1!');
await page.locator('fluent-dropdown#locationId').click();       // location is required
await page.waitForTimeout(500);
await page.locator('fluent-option:visible').first().click();
await page.locator('.fluent-stack-horizontal.login-submit-row').click();
await page.waitForTimeout(4000);                                 // lands on /
```

Selector notes, all of which cost time when guessed wrong:
- Use `getByRole('textbox', ...)`, **not** `getByPlaceholder(...)`. The placeholder matches the `<fluent-text-input>` custom element as well as the inner `<input>`, and `.fill()` on the custom element fails with "Element is not an `<input>`...". Whether `.first()` or `.last()` is the real input flips depending on how much of the placeholder string you match, so don't rely on either.
- Filling the inner `<input>` via a raw CSS selector "works" but submits **empty** — the value never reaches the form-associated custom element, and the POST comes back `400` with "The username field is required".
- The location control is `fluent-dropdown#locationId`, not `fluent-select#locationId`.
- Submit is `.fluent-stack-horizontal.login-submit-row`. The older `.fluent-stack-vertical > .fluent-stack-horizontal` now matches 3 elements and fails Playwright strict mode.

## Fluent UI v5 driving gotchas
- Dialog content is slotted: locate via the `fluent-dialog` element, NOT `[role="alertdialog"]` (that native element only contains a `<slot>`).
- `fluent-option` elements from *closed* dropdowns elsewhere on the page remain in the DOM — scope option clicks with `:visible` or you'll hit the wrong list.
- Toolbar icon buttons have no text; identify by tooltip (`aria-describedby` → tooltip element text) or position. On `/manage/dashboards/{id}`: buttons in order are name, Save, preview-toggle, `#DashboardWidgetList` (add widget), Delete.
- Dashboard edit page loads in preview mode; right-click-to-configure only works in edit-layout mode (toggle the preview button first). Widget hosts: `.dashboard-widget-host`.
- Escape closes Fluent dialogs (fires the dismiss/cancel path).
- Nav items rendered from `FluentNavItem`/`FluentAppBarItem` are easy to mis-target: the same label can match several elements, including zero-sized copies in a collapsed nav. Filter on a non-zero `getBoundingClientRect()` before clicking, or you will "click" something invisible and see nothing happen.

## Timing measurements — clear CDP throttling
`Network.emulateNetworkConditions` sticks to the *page*, not the CDP session that set it. Opening a new session and setting `latency: 0` does **not** reliably undo an earlier `latency: 3000`, so every later measurement silently inherits it — which looks exactly like the app being slow. If timings seem implausible, sanity-check with a `fetch()` of a static file (should be ~2ms locally); if it isn't, close the page (`browser_close`) and navigate again to get a clean context.

## Useful flows
- Dashboards list: `/manage/dashboards` ("Create blank dashboard" button + row click to edit).
- Music page: `/music`.
- Verify persisted widget state directly: `SELECT ... FROM "DashboardWidgets" WHERE "Type" = <n>` via postgres MCP.

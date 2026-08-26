---
name: verify
description: Build, launch, and drive the Lanyard server app to verify changes end-to-end (Blazor UI via Playwright MCP, Postgres via docker).
---

# Verifying LanyardApp changes

## Prerequisites
- Docker Postgres must be running: container `lanyard-postgres` (localhost:5432, db/user `lanyard_dev`, password `lanyard_dev_password`). Check: `docker ps`.
- Don't build `LanyardApp.sln` (or the `.slnx`) as a whole — it fails with `NETSDK1147: workloads must be installed: maui-android` because `Lanyard.Reach` targets `net10.0-android` and that workload isn't installed in this sandbox. Build only the server project directly:
  `dotnet build src/Lanyard.Server/LanyardApp/Lanyard.App.csproj`
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
There's no dedicated screenshot skill; it's just two calls:
1. `mcp__playwright__browser_navigate` to the page, then `mcp__playwright__browser_take_screenshot` (`scale: "css"`, give it a `filename`) — saves a PNG to the repo root.
2. `Read` that PNG path — this renders the image inline in the conversation so the user actually sees it (a screenshot call alone is invisible to them).

For anything below the fold (e.g. a footer link), `browser_resize` to a taller viewport first, or the screenshot will only show what's visible at default size. Delete the PNG files (`rm -f *.png` at repo root, or scope to the filenames used) once you're done — they're scratch output, not something to leave lying around or commit.

## Login (Playwright MCP)
Navigate to `http://localhost:5096/login?returnUrl=/<target>`, then:
- `page.getByPlaceholder('Username').last().fill('bmercer')` (Fluent inputs resolve to 2 elements — use `.last()`)
- password is in the `reference_lanyard_login` memory
- submit control: `page.locator('.fluent-stack-vertical > .fluent-stack-horizontal').click()`

## Fluent UI v5 driving gotchas
- Dialog content is slotted: locate via the `fluent-dialog` element, NOT `[role="alertdialog"]` (that native element only contains a `<slot>`).
- `fluent-option` elements from *closed* dropdowns elsewhere on the page remain in the DOM — scope option clicks with `:visible` or you'll hit the wrong list.
- Toolbar icon buttons have no text; identify by tooltip (`aria-describedby` → tooltip element text) or position. On `/manage/dashboards/{id}`: buttons in order are name, Save, preview-toggle, `#DashboardWidgetList` (add widget), Delete.
- Dashboard edit page loads in preview mode; right-click-to-configure only works in edit-layout mode (toggle the preview button first). Widget hosts: `.dashboard-widget-host`.
- Escape closes Fluent dialogs (fires the dismiss/cancel path).

## Useful flows
- Dashboards list: `/manage/dashboards` ("Create blank dashboard" button + row click to edit).
- Music page: `/music`.
- Verify persisted widget state directly: `SELECT ... FROM "DashboardWidgets" WHERE "Type" = <n>` via postgres MCP.

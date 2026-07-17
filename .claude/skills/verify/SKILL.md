---
name: verify
description: Build, launch, and drive the Lanyard server app to verify changes end-to-end (Blazor UI via Playwright MCP, Postgres via docker).
---

# Verifying LanyardApp changes

## Prerequisites
- Docker Postgres must be running: container `lanyard-postgres` (localhost:5432, db/user `lanyard_dev`, password `lanyard_dev_password`). Check: `docker ps`.
- Build with `dotnet build LanyardApp.sln` (NOT the `.slnx` mentioned in CLAUDE.md).

## Migrations
- **Migrations do NOT auto-apply in Development** — `Program.cs` only calls `MigrateAsync()` when `IsDevelopment() == false`. After scaffolding a migration, apply it manually:
  `dotnet ef database update --project src/Lanyard.Infrastructure --no-build`
- Use the `postgres` MCP tools for seed/inspect queries; `docker exec ... psql -c "..."` mangles quoting through PowerShell.

## Launch
```
dotnet run --project src/Lanyard.Server/LanyardApp/Lanyard.App.csproj --launch-profile http --no-build
```
(run in background; app listens on http://localhost:5096; wait for the port with Test-NetConnection). Stop with TaskStop when done.

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

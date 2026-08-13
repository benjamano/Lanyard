# AGENTS.md

This file is the contributor/agent operating manual for the entire repository.

## Scope

This file applies to this folder and all subfolders.

BEFORE STARTING EVERY REQUEST, WRITE THE MESSAGE: "Instructions Loaded" TO CONFIRM YOU HAVE READ THESE INSTRUCTIONS.

## MCP Preference For This Repository

When working in this repository, for any Blazor or Fluent UI Blazor question/task:

1. Call the `blazor_knowledge` MCP server first.
2. Use these tools/resources before relying on built-in memory:
   - `search_blazor_docs`
   - `semantic_search_blazor_docs`
   - `get_fluentui_component`
   - `compare_patterns`
   - `blazor://overview`
   - `blazor://component/{name}`
   - `blazor://api/{symbol}`
   - `blazor://search/{query}`
   - `blazor://example/{component}/{scenario}`
3. Prefer answers that include citations returned by the MCP server.
4. If MCP returns no relevant results, fall back to general reasoning/web sources and clearly state the fallback.

## Solution Overview

Lanyard is a .NET 10 solution (`LanyardApp.sln` at repo root) with a layered architecture and several runtime frontends. Real project paths are nested under `src/` — there are no top-level `LanyardData`/`LanyardServices`/`LanyardAPI` folders, those live under `src/Lanyard.Server/`:

- `src/Lanyard.Server/LanyardApp` (csproj `Lanyard.App.csproj`):
  Blazor Server app (Interactive Server rendering) and the main staff/customer web UI.
- `src/Lanyard.Server/LanyardAPI` (csproj `Lanyard.API.csproj`):
  HTTP API controllers for auth, music, and file management endpoints.
- `src/Lanyard.Server/LanyardServices` (csproj `Lanyard.Services.csproj`):
  Business logic and orchestration layer used by app/API.
- `src/Lanyard.Infrastructure`:
  EF Core data access, entity models, and migrations.
- `src/Lanyard.Shared`:
  Cross-process DTOs/enums shared by server and WPF client.
- `src/Lanyard.Client`:
  .NET 10 WPF/console hybrid kiosk client connected over SignalR. The built/runnable binary is `build\Lanyard.Client.exe` at the repo root (the csproj sets `OutputPath=..\..\build\`) — `src/Lanyard.Client/bin/...` output is not what actually runs.
- `src/Lanyard.Client.Watchdog`:
  Console supervisor process for `Lanyard.Client.exe` — launches it, hides its own window, and auto-restarts it on crash (with a crash-loop guard: gives up after 5 rapid crashes within 30s).
- `src/Lanyard.Tests` (csproj `Lanyard.Tests.csproj`):
  MSTest suite for service-level behavior and regression coverage.
- `src/Lanyard.Reach/*` — a satellite marketing/booking site for the business, sharing one Blazor UI across two hosts:
  - `Lanyard.Reach.Shared`: Razor class library with the actual pages/layout (home, pricing, locations, cookies/privacy/terms) plus a `RedirectToLanyardServer` page that redirects out to the main Lanyard server.
  - `Lanyard.Reach`: .NET MAUI Blazor Hybrid app (Android/iOS/MacCatalyst/Windows) hosting `Lanyard.Reach.Shared` in a native app shell.
  - `Lanyard.Reach.Web`: ASP.NET Core web project hosting the same `Lanyard.Reach.Shared` UI as an interactive server-rendered website — the browser counterpart to the MAUI app.

## Core Programming Paradigms In This Repo

1. Layered architecture with clear boundaries:
   UI/API -> Application services -> Infrastructure/data -> SQL.
2. Interface-driven service design:
   Add and consume service interfaces where practical (`I*Service` patterns).
3. Result-wrapper error model:
   Service methods generally return `Result<T>` (`Ok`/`Fail`) rather than throwing for expected validation failures.
4. Async-first IO:
   Database, file, and network operations should be `async` and use cancellation tokens for cancellable workflows.
5. Soft-delete and active filtering:
   Many entities use `IsActive`; reads should respect active-state unless there is a reason not to.
6. Real-time event-driven updates:
   SignalR hub and clients propagate music/projection state to connected kiosk clients.
7. Dependency injection as composition root:
   Service registration and app wiring happen in `LanyardApp/Program.cs`.

## Key Domain Concepts

1. Projection system:
   Template -> Template parameters -> Program -> Program steps -> Per-step parameter values.
2. Client projection mapping:
   A client can have multiple projection settings per display, each bound to a projection program.
3. Music system:
   Songs, playlists, playlist members, and remote playback control via SignalR.
4. File system:
   Physical files under `UploadedFiles` with DB metadata (`FileMetadata`) and optional folder hierarchy (`Folder`).
5. Identity and roles:
   `UserProfile` + `ApplicationRole` (with extra metadata and `IsActive` role semantics).

## Conventions For Adding Code

### 1) Choose the correct project first

- UI page/component behavior: `src/Lanyard.Server/LanyardApp/Components/...`
- API endpoint: `src/Lanyard.Server/LanyardAPI/Controllers/...`
- Business logic: `src/Lanyard.Server/LanyardServices/Services/...`
- Entity schema/migrations: `src/Lanyard.Infrastructure/Models` and `src/Lanyard.Infrastructure/Migrations`
- Shared contract needed by server + client: `src/Lanyard.Shared/...`
- Kiosk runtime behavior: `src/Lanyard.Client/...`
- Tests: `src/Lanyard.Tests/...`

### 2) Keep responsibilities narrow

- Controllers should be thin; delegate real logic to services.
- Services should contain validation + orchestration + persistence calls.
- Components should focus on state + rendering + invoking services.

### 3) Follow existing type/error patterns

- Prefer explicit types over `var` (except anonymous type scenarios).
- Use `Result<T>.Ok(...)` and `Result<T>.Fail(...)` in service methods.
- Return user-meaningful error text for failure paths.

### 4) Data access patterns

- Use `IDbContextFactory<ApplicationDbContext>` in services.
- Use `.AsNoTracking()` for read-only query paths when updates are not needed.
- Include related data explicitly (`Include`/`ThenInclude`) where required.
- When creating detached graph entities, avoid unwanted EF tracking collisions.

### 5) Blazor component patterns

- Use `[Parameter]` for component inputs.
- Prefer `Task` for async handlers (`async void` only for true event callbacks where unavoidable).
- Dispose/cleanup long-running operations and cancellation token sources.
- Use Fluent UI components already in use across the project.
- Prefer Bootstrap utility/classes for primary layout and spacing styling when possible.
- Add custom CSS only when Bootstrap/Fluent component parameters cannot reasonably achieve the required result.
- **Never use inline styling**: no raw `style="..."`/`Style="..."` and no embedded `<style>` blocks in a `.razor` file (unscoped/global, leaks across the whole app). Use that component's `<ComponentName>.razor.css` scoped stylesheet instead — see the `fluentui-v5-blazor` skill for why `::deep` is often required on Fluent component roots and how it can silently no-op.
- **Spacing**: Fluent's defaults (unset `FluentStack` gaps, `FluentGrid`/`FluentGridItem` `Xs` splits) read as too cramped for this app — always set an explicit `FluentStack` gap, erring toward more space, not less. Observed reference values: `HorizontalGap="6"` for tightly related inline fields, `VerticalGap="18px"` for stacked form sections, `HorizontalGap="8px"` for button rows/inline pairs. For asymmetric multi-field rows (e.g. a wide field next to a narrow one), prefer a `FluentStack` wrapping plain `<div>`s with explicit proportional widths over `FluentGridItem Xs="n"` splits — gives exact control instead of the coarser 12-column grid. Fine vertical misalignment between sibling fields gets nudged with a plain `mt-5` utility class on a wrapping div, not a Fluent spacing parameter. Give dividers/panes room too: a `FluentDivider` between sections gets `Class="my-3"` (not `my-2`), multi-splitter panes get `Class="px-2"`/`Class="px-3"` padding rather than sitting flush against the edge.
- **Icon-only buttons need a `Title`**: any `FluentButton` using only `IconStart`/`IconEnd` with no visible text content must set `Title="..."` — otherwise it has no accessible name for a screen reader. This isn't yet consistently applied across the codebase (most existing icon buttons predate the rule), but new/edited icon buttons should always include it.
- **Mobile-first for new UI**: actively design new pages/components for phone-sized viewports rather than shrinking a desktop layout after the fact — be willing to propose a different flow (e.g. sequential drill-down instead of side-by-side master/detail) for narrow screens. Not a mandate to retroactively rework shipped pages.

### 6) SignalR patterns

- Keep server/client hub event names consistent.
- Log meaningful connection and command events.
- Avoid fire-and-forget if delivery guarantees matter.

## Blazor + Fluent UI Guidance (MCP-backed)

Use MCP docs first for implementation details.

- Render modes and interactive behavior:
  `https://learn.microsoft.com/aspnet/core/blazor/components/render-modes`
- Component lifecycle and disposal:
  `https://learn.microsoft.com/aspnet/core/blazor/components/lifecycle`
- Forms validation:
  `https://learn.microsoft.com/aspnet/core/blazor/forms/validation`
- FluentDataGrid behavior and caveats:
  `https://fluentui-blazor.azurewebsites.net/datagrid`

Important FluentDataGrid note from docs:
- Do not use `RowStyle` for dynamic post-render row state updates; prefer `RowClass`.

## Security And Auth Rules

1. Never hardcode production secrets.
2. Validate user input at API/service boundaries.
3. Enforce authorization on staff/admin routes and endpoints as needed.
4. Keep identity operations in service/controller layers, not UI-only logic.
5. Use least privilege; do not expose admin-only workflows accidentally.
6. **Every `@page` route must declare `@attribute [Authorize]`/`[Authorize(Roles = "...")]` or `@attribute [Microsoft.AspNetCore.Authorization.AllowAnonymous]`** — there is no default-allow path (`RouteAuthorizationGate.razor` enforces this). See the `route-authorization` skill for history and edge cases.
7. **`_currentLocationContext`/`LocationScope` is not an app-wide tenancy filter** — it's only implemented for the Training/Course module. See the `location-scoping` skill before assuming any query in another module is already location-filtered — it isn't, because the concept doesn't exist there yet.

## Database And Migration Workflow

When changing EF models:

1. Update entities in `src/Lanyard.Infrastructure/Models`.
2. Add migration in `src/Lanyard.Infrastructure/Migrations`.
3. Verify startup project compatibility (`src/Lanyard.Server/LanyardApp`).
4. Update dependent DTOs/services/tests.

Commands (from repository root):

```powershell
dotnet ef migrations add <MigrationName> --project src/Lanyard.Infrastructure --startup-project src/Lanyard.Server/LanyardApp
dotnet ef database update --project src/Lanyard.Infrastructure --startup-project src/Lanyard.Server/LanyardApp
```

There is no `IDesignTimeDbContextFactory`, so `dotnet ef` builds the App startup project to resolve the `DbContext` — these commands will fail while the app is running under the debugger (file locks).

Rule:
- Ask for confirmation before destructive schema changes (dropping/renaming columns or tables, or data-destructive migrations).

## Build, Run, Test

Common commands:

```powershell
dotnet restore
dotnet build LanyardApp.sln
dotnet test src/Lanyard.Tests/Lanyard.Tests.csproj
```

App runtime defaults:
- Web app URL from launch settings: `https://localhost:7175` (plus HTTP port).
- WPF client expected env vars:
  - `SIGNALR_SERVER_URL` (example: `https://localhost:7175/websocket`)
  - `KIOSK_SERVER_URL` (example: `https://localhost:7175/staff/kiosk`)
  - `OTEL_EXPORTER_OTLP_ENDPOINT` (optional; example: `http://<home-server-ip>:5341`) — exports logs to the self-hosted Seq instance, see `deploy/seq/docker-compose.yml`. Same env var applies to the server app.

For end-to-end verification beyond a plain build/test — running the app, driving the UI, or launching the full server+kiosk-client stack — see the `verify` and `kiosk-client-dev-stack` skills (the latter also explains why a full solution build can be blocked by file locks while the app runs under a debugger, and how to work around it).

## Development Seeding Notes

In development, startup seeding creates default roles and an admin user.
If troubleshooting local auth, inspect `src/Lanyard.Server/LanyardApp/Data/DevelopmentDataSeeder.cs` first.

## Testing Expectations

1. Place tests in `src/Lanyard.Tests` with folder parity to source area.
2. Use Arrange-Act-Assert structure.
3. Cover success and failure paths for new service methods.
4. For data logic tests, prefer EF InMemory patterns already used by existing tests.

## Practical Checklist For Any New Feature

1. Confirm which layer(s) the change belongs to.
2. Add or update model/DTO/contracts first.
3. Implement service logic with `Result<T>` semantics.
4. Add/update API and/or Blazor UI wiring.
5. Add/update SignalR contracts if the feature is real-time.
6. Add migrations if schema changed.
7. Add or update tests.
8. Build and run tests before completion.

## Deep-Dive Skills

Area-specific gotchas — Fluent UI v5, charting, route authorization history, the email/invite system, client build troubleshooting, the kiosk dev stack, dashboard widgets, location scoping, the DMX/automation engines, service testing patterns, and API controller conventions — live in skills rather than in this file. See the table in `CLAUDE.md`'s "Deep-Dive Skills" section for the full list.

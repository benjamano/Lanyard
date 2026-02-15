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

Lanyard is a .NET 10 solution with a layered architecture and two runtime frontends:

- `LanyardApp`:
  Blazor Server app (Interactive Server rendering) and the main staff/customer web UI.
- `LanyardAPI`:
  HTTP API controllers for auth, music, and file management endpoints.
- `LanyardServices`:
  Business logic and orchestration layer used by app/API.
- `LanyardData`:
  EF Core data access, entity models, and migrations.
- `Lanyard.Shared`:
  Cross-process DTOs/enums shared by server and WPF client.
- `LanyardClient`:
  .NET 10 WPF/console hybrid kiosk client connected over SignalR.
- `LanyardTests`:
  MSTest suite for service-level behavior and regression coverage.

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

- UI page/component behavior: `LanyardApp/Components/...`
- API endpoint: `LanyardAPI/Controllers/...`
- Business logic: `LanyardServices/Services/...`
- Entity schema/migrations: `LanyardData/Models` and `LanyardData/Migrations`
- Shared contract needed by server + client: `Lanyard.Shared/...`
- Kiosk runtime behavior: `LanyardClient/...`
- Tests: `LanyardTests/...`

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

## Database And Migration Workflow

When changing EF models:

1. Update entities in `LanyardData/Models`.
2. Add migration in `LanyardData/Migrations`.
3. Verify startup project compatibility (`LanyardApp`).
4. Update dependent DTOs/services/tests.

Commands (from repository root):

```powershell
dotnet ef migrations add <MigrationName> --project LanyardData/Lanyard.Infrastructure.csproj --startup-project LanyardApp/Lanyard.App.csproj
dotnet ef database update --project LanyardData/Lanyard.Infrastructure.csproj --startup-project LanyardApp/Lanyard.App.csproj
```

Rule:
- Ask for confirmation before destructive schema changes (dropping/renaming columns or tables, or data-destructive migrations).

## Build, Run, Test

Common commands:

```powershell
dotnet restore
dotnet build LanyardApp.slnx
dotnet test LanyardTests/Lanyard.Tests.csproj
```

App runtime defaults:
- Web app URL from launch settings: `https://localhost:7175` (plus HTTP port).
- WPF client expected env vars:
  - `SIGNALR_SERVER_URL` (example: `https://localhost:7175/websocket`)
  - `KIOSK_SERVER_URL` (example: `https://localhost:7175/staff/kiosk`)

## Development Seeding Notes

In development, startup seeding creates default roles and an admin user.
If troubleshooting local auth, inspect `LanyardApp/Data/DevelopmentDataSeeder.cs` first.

## Testing Expectations

1. Place tests in `LanyardTests` with folder parity to source area.
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

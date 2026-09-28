# CLAUDE.md

This file supplements `AGENTS.md` with deeper pattern explanations and Claude-specific behaviour rules.

BEFORE STARTING EVERY REQUEST, WRITE THE MESSAGE: "Instructions Loaded" TO CONFIRM YOU HAVE READ THESE INSTRUCTIONS.

## Read This First

`AGENTS.md` is the primary contributor operating manual. Read it before making structural or architectural decisions. This file adds the detail that AGENTS.md summarises.

---

## MCP Preference

See AGENTS.md's "MCP Preference For This Repository" — same rule for both files, kept in one place to avoid drift.

---

## Core Patterns

### `Result<T>` Pattern (`src/Lanyard.Infrastructure/DTO/Result.cs`)

Services return `Result<T>` (`.Ok(data)` / `.Fail(error)`) instead of throwing for expected failures — callers inspect `.IsSuccess`, no guessing which exceptions to catch. Exceptions are reserved for truly unexpected conditions and caught at the service boundary, not propagated.

- Every service method that can fail predictably returns `Task<Result<T>>`.
- The `catch` block returns `Result<T>.Fail(ex.Message)` — never rethrows or swallows.
- Business-rule failures ("not found", "validation failed") use `.Fail(...)` directly, no exception needed.
- Use `.Fail()` — `.Error()` does not exist on this type.

```csharp
try { return Result<T>.Ok(data); }
catch (Exception ex) { return Result<T>.Fail($"...: {ex.Message}"); }
```
See `src/Lanyard.Server/LanyardServices/Services/Playlists/PlaylistService.cs` for a full example.

### `.AsNoTracking()`

Chain on every query whose results won't be updated within the same `DbContext` scope (the default for all reads) — skips EF Core's change-tracking snapshot, which is pure overhead for read-only queries.

### `.TagWithCallSite()`

Chain alongside `.AsNoTracking()` on any meaningful read query. Embeds the calling file/line as a SQL comment, so a slow query in `pg_stat_statements` or server logs traces straight back to the code that issued it.

### `IDbContextFactory<ApplicationDbContext>`

Always inject the factory, never `ApplicationDbContext` directly — singleton services (`MusicPlayerService`, `AutomationEngineService`, `DmxService`) outlive a request-scoped `DbContext`, causing lifetime mismatches and thread-safety issues. Use `await using` with `CreateDbContextAsync()`; one context per logical unit of work, never shared across concurrent operations.

### Interface-Driven Services

Every service has a matching `I*Service` interface; `Program.cs` registers the interface against the concrete class; components/controllers inject the interface, never the concrete type. This is what makes the Moq-based test suite possible — Moq can't substitute a concrete type.

**Pre-injected via `src/Lanyard.Server/LanyardApp/Components/_Imports.razor`** — check its `@inject` lines before injecting a service yourself.

---

## Soft-Delete Conventions

Two patterns exist — check the model to know which applies:

| Field | Filter to apply |
|---|---|
| `IsActive` | `.Where(x => x.IsActive)` |
| `DeleteDate` | `.Where(x => x.DeleteDate == null)` |

Always apply the appropriate filter on reads unless the call is explicitly about fetching inactive/deleted records. Never hard-delete a row unless there is an explicit business or legal reason — ask the user for confirmation first.

---

## SignalR Event Patterns

- **Hub event names must match exactly** between the server's `SendAsync("EventName", ...)` call and the client's `.On("EventName", ...)` registration. A mismatch silently drops the event with no error.
- **Prefer targeted sends** (`Clients.Client(connectionId)`) over `Clients.All` unless the event genuinely applies to every connected client.
- **Do not fire-and-forget** (`_ = hub.SendAsync(...)`) when delivery confirmation matters — `await` the call.
- **Log connection, disconnection, and command dispatch events** at `Information` level so the server log tells you what happened without needing a debugger attached.

---

## Logging Conventions

Plain `ILogger<T>` — no Serilog, no correlation-ID/trace-ID propagation anywhere in the app. Don't introduce either without a separate decision to do so; there's nothing existing to plug into.

- Always use structured templates, never string interpolation: `_logger.LogWarning("Failed to add {UserId} to {LocationId}: {Error}", userId, locationId, error)`, not `$"Failed to add {userId}..."`.
- `LogInformation` for normal lifecycle events, `LogWarning` for recoverable/expected failures (including passing a `Result.Error` string as a templated property), `LogError(ex, ...)` for caught exceptions.

---

## Claude-Specific Behaviour Rules

### Before acting
- Read `AGENTS.md` before making structural or architectural decisions.
- For Blazor/FluentUI tasks, query the `fluent-ui-blazor` MCP server first (see AGENTS.md).

### Ask for confirmation before
- Force-pushing any branch.
- Deleting files that are not obviously temporary.

### Migration safety
`dotnet ef database update` may be run automatically for any migration, including destructive ones (dropping/renaming a column or table, or a migration whose `Down()` method loses data) — no approval needed first. Still describe what the migration does (and flag if it's destructive) before running it, so the action is visible, but do not wait for a go-ahead.

### Before marking a task complete
Run `dotnet build LanyardApp.sln` and `dotnet test src/Lanyard.Tests/Lanyard.Tests.csproj` and confirm both pass.

### Release notes — update whenever a change is user-noticeable
If a change would be noticeable to a user (new feature, behaviour change, visible bug fix, new email, new UI element, etc. — not internal refactors, test-only changes, or dev-tooling tweaks), add an entry to `src/Lanyard.Server/LanyardServices/Services/ReleaseNotes/release-notes.json` describing it, and bump `<Version>` in `src/Lanyard.Server/LanyardApp/Lanyard.App.csproj` to match the new release-notes entry's `version`. Do this as part of the same piece of work — don't wait to be asked.

### UI screenshots go on the PR

If a change is user-noticeable UI, its PR must carry the screenshots — phone (390x844) and desktop (1440x900). Publish them with `.claude/scripts/publish-screenshots.sh`; see the `verify` skill for the capture-and-publish loop.

Inline-only screenshots are not enough. An image attached in chat lives solely in the transcript, and the transcript loses images — they disappear permanently, which is exactly the problem this script exists to solve.

For a flow a still image can't show, a short video is optional — see the `verify` skill for recording and publishing it.

### Merge authority -- dev yes, main never
The installed Claude GitHub App (triggered by an `@claude` mention on a PR/issue comment) is allowed to merge a pull request into `dev` on its own, but ONLY once the required CI checks (`build-and-test`, `docker-build`) are passing -- that's enforced by a separate ruleset ("Dev Requires CI To Pass") the App has no bypass on, so it cannot merge a red PR regardless of what it believes about the change.

The App has NO bypass on `main`'s ruleset ("Do Not Push To Main") at all -- this isn't a policy instruction being trusted to hold, it's enforced by GitHub itself. Do not attempt to work around this (e.g. by pushing directly, or merging `dev` into `main` some other way) -- releases to `main` go through the normal human-reviewed release PR process (see recent "RELEASE x.y.z" PRs for the pattern).

If you (an interactive session, not the installed App) are asked to merge something, the same rule applies by policy even though your credentials may not be technically restricted the same way: merge into `dev` only after CI is green, never touch `main` directly.

### Route authorization — the one rule that must never get missed
Every `@page` needs `@attribute [Authorize]`/`[Authorize(Roles = "...")]` or `@attribute [Microsoft.AspNetCore.Authorization.AllowAnonymous]` — there is no third option; a missing attribute redirects to login by design (`RouteAuthorizationGate.razor`). For the history, edge cases (`StaffNotFound.razor`), and why this matters, see the `route-authorization` skill.

---

## Deep-Dive Skills

Topic deep-dives (DMX, automation, dashboards, routes, charts, Fluent UI, testing, client release) live in `.claude/skills/` and load on demand — check the skill list before touching those areas.

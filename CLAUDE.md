# CLAUDE.md

This file supplements `AGENTS.md` with deeper pattern explanations and Claude-specific behaviour rules.

BEFORE STARTING EVERY REQUEST, WRITE THE MESSAGE: "Instructions Loaded" TO CONFIRM YOU HAVE READ THESE INSTRUCTIONS.

## Read This First

`AGENTS.md` is the primary contributor operating manual. Read it before making structural or architectural decisions. This file adds the detail that AGENTS.md summarises.

---

## MCP Preference

For any Blazor or Fluent UI Blazor question or task, call the `blazor_knowledge` MCP server **before** relying on built-in knowledge. The server name in `.claude/settings.json` is `fluent-ui-blazor`.

Key tools:
- `search_blazor_docs` / `semantic_search_blazor_docs`
- `get_fluentui_component`
- `compare_patterns`
- `blazor://overview`, `blazor://component/{name}`, `blazor://api/{symbol}`, `blazor://example/{component}/{scenario}`

If the MCP server returns no relevant results, fall back to general reasoning and state the fallback explicitly.

---

## Core Patterns

### `Result<T>` Pattern

**Definition**: `src/Lanyard.Infrastructure/DTO/Result.cs`

```csharp
public record Result<T>
{
    public bool IsSuccess { get; init; }
    public bool Success => IsSuccess;
    public T? Data { get; init; }
    public string? Error { get; init; }

    public static Result<T> Ok(T data) => new() { IsSuccess = true, Data = data };
    public static Result<T> Fail(string error) => new() { IsSuccess = false, Error = error };
}
```

**Why**: Services return `Result<T>` instead of throwing for expected failures. Callers always inspect `.IsSuccess` — no guessing which exceptions to catch. Exceptions are only used for truly unexpected conditions and are caught at the service boundary, not propagated.

**Rules**:
- Every service method that can fail in a predictable way returns `Task<Result<T>>`.
- The `catch` block returns `Result<T>.Fail(ex.Message)` — never rethrows or swallows.
- Business-rule failures (e.g. "not found", "validation failed") use `Result<T>.Fail(...)` directly, without an exception.
- Use `.Fail()` not `.Error()` — `.Error()` does not exist on this type.

**Canonical example** (`src/Lanyard.Server/LanyardServices/Services/Playlists/PlaylistService.cs`):

```csharp
public async Task<Result<IEnumerable<Playlist>>> GetActivePlaylistsAsync()
{
    try
    {
        ApplicationDbContext context = _factory.CreateDbContext();

        IEnumerable<Playlist> playlists = await context.Playlists
            .AsNoTracking()
            .Where(playlist => playlist.DeleteDate == null)
            .Include(x => x.Members!)
            .ThenInclude(x => x.Song)
            .ToListAsync();

        return Result<IEnumerable<Playlist>>.Ok(playlists);
    }
    catch (Exception ex)
    {
        return Result<IEnumerable<Playlist>>.Fail($"An error occurred while retrieving active playlists: {ex.Message}");
    }
}
```

---

### `.AsNoTracking()`

**Why**: EF Core's change tracker takes a snapshot of every entity it loads so it can detect modifications. For read-only queries that will never be saved back, this overhead is pure waste. `.AsNoTracking()` skips the snapshot entirely.

**Rule**: Chain `.AsNoTracking()` on every query whose results will not be updated within the same `DbContext` scope. This is the default for all read operations.

**Canonical example** (`src/Lanyard.Server/LanyardServices/Services/Dashboards/DashboardService.cs`):

```csharp
List<Dashboard> dashboards = await ctx.Dashboards
    .AsNoTracking()
    .Where(x => x.IsActive)
    .Include(x => x.Widgets.Where(w => w.IsActive))
    .OrderBy(x => x.Name)
    .ToListAsync();
```

---

### `.TagWithCallSite()`

**Why**: This EF Core extension embeds the calling C# file path and line number as a comment in the generated SQL. When a slow query appears in PostgreSQL's `pg_stat_statements` or server logs, the comment tells you exactly which line of application code produced it — without needing to trace back through query shapes.

**Rule**: Chain `.TagWithCallSite()` alongside `.AsNoTracking()` on any meaningful read query (i.e. any query you would want to identify in a database trace).

**Canonical example** (`src/Lanyard.Server/LanyardServices/Services/MusicPlayer/MusicPlayerService.cs`):

```csharp
Playlist? playlist = await context.Playlists
    .AsNoTracking()
    .TagWithCallSite()
    .Where(x => x.Id == playlistId)
    .FirstOrDefaultAsync();
```

---

### `IDbContextFactory<ApplicationDbContext>` Usage

**Why**: A scoped `DbContext` lives for the duration of an HTTP request. Singleton services (`MusicPlayerService`, `AutomationEngineService`, `DmxService`) outlive any request, so injecting a scoped `DbContext` directly causes a lifetime mismatch and thread-safety issues. The factory creates a fresh, short-lived `DbContext` per operation and disposes it cleanly.

**Rules**:
- Always inject `IDbContextFactory<ApplicationDbContext>` — never `ApplicationDbContext` directly.
- Use `await using` with `CreateDbContextAsync()` to ensure disposal even on exceptions.
- Create one context per logical unit of work; do not share a context across concurrent operations.

**Canonical example** (`src/Lanyard.Server/LanyardServices/Services/Dashboards/DashboardService.cs`):

```csharp
public class DashboardService(IDbContextFactory<ApplicationDbContext> factory) : IDashboardService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<IEnumerable<Dashboard>>> GetDashboardsAsync()
    {
        await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
        // ...
    }
}
```

---

### Interface-Driven Services

**Rule**: Every service must have a matching `I*Service` interface. `Program.cs` registers the interface against the concrete class. Blazor components and controllers always inject the interface — never the concrete type.

**Why**: The interface is the only thing the test project mocks. If components take the concrete type, Moq cannot substitute a fake, and the entire MSTest suite breaks.

**Pattern**:
```csharp
// Registration in Program.cs
builder.Services.AddScoped<IPlaylistService, PlaylistService>();
```

```razor
@* Injection in a Blazor component — prefer @inject over [Inject] attribute *@
@inject IPlaylistService PlaylistService
```

#### Pre-injected services via `_Imports.razor`

`src/Lanyard.Server/LanyardApp/Components/_Imports.razor` globally injects several services into every component in the app. Do **not** re-inject these in individual components — they are already available under these field names:

| Field name | Interface |
|---|---|
| `_securityService` | `ISecurityService` |
| `_dialogService` | `IDialogService` |
| `_timeService` | `ITimeService` |

The same file also `@using`-imports the most common namespaces (`Lanyard.Infrastructure.Models`, `Lanyard.Infrastructure.DTO`, `Microsoft.FluentUI.AspNetCore.Components`, etc.), so those do not need repeating in individual `.razor` files either.

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

## Claude-Specific Behaviour Rules

### Before acting
- Read `AGENTS.md` before making structural or architectural decisions.
- For Blazor/FluentUI tasks, query the `blazor_knowledge` MCP server first (see above).

### Ask for confirmation before
- Any destructive migration: dropping or renaming a column or table, or any migration whose `Down()` method loses data.
- Force-pushing any branch.
- Deleting files that are not obviously temporary.

### Migration safety
Never run `dotnet ef database update` automatically for destructive schema changes. Describe the migration and wait for explicit approval. Safe additive migrations (new table, new nullable column) can proceed without asking.

### Before marking a task complete
Run `dotnet build LanyardApp.slnx` and `dotnet test LanyardTests/Lanyard.Tests.csproj` and confirm both pass.

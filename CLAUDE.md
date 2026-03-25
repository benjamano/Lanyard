# LanyardApp — Claude Context

## Project Overview

LanyardApp is a real-time arcade game management system for controlling music playback, laser game monitoring, and projection programs across remote client machines from a centralized Blazor web dashboard. It was built for an indoor laser tag / arcade venue.

### Projects

| Project | Type | Purpose |
|---|---|---|
| `Lanyard.Shared` | Class library | Cross-project DTOs and enums — no dependencies on other projects |
| `LanyardData` | Class library | EF Core data access layer — models, DbContext, migrations, DTOs |
| `LanyardServices` | Class library | Business logic services and SignalR hub |
| `LanyardAPI` | ASP.NET Core Web API | Audio file streaming (`GET /api/music/audio/{id}`) |
| `LanyardApp` | Blazor Server | Staff/management web dashboard |
| `LanyardClient` | WPF/Console app | Remote client on arcade machines — plays music, sniffs game packets, runs projections |
| `LanyardTests` | MSTest | Unit tests for services |

### Dependency Graph

```
Lanyard.Shared  ←  LanyardData  ←  LanyardServices  ←  LanyardApp
                                  ↑                   ↑
                                  LanyardAPI          LanyardTests
LanyardClient → Lanyard.Shared, LanyardData (DTOs only)
```

---

## Technology Stack

- **.NET 10.0** — all projects
- **Blazor Server** (`net10.0`) — interactive server-side rendering for the web dashboard
- **WPF** (`net10.0-windows`) — LanyardClient UI host
- **PostgreSQL** via `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0`
- **EF Core 10.0.x** — Code First, DbContextFactory pattern
- **ASP.NET Identity** — `UserProfile` (extends `IdentityUser`), `ApplicationRole` (extends `IdentityRole`)
- **SignalR** — server hub in `LanyardServices`; client in `LanyardClient` using `Microsoft.AspNetCore.SignalR.Client 10.0.1`
- **Azure SignalR** (optional production backing, configured in `LanyardApp.csproj`)
- **NAudio 2.2.1** — audio decoding and playback in `LanyardClient`
- **SharpPcap 6.3.1** + **PacketDotNet 1.4.8** — UDP packet capture in `LanyardClient`
- **Microsoft Fluent UI Components 4.13.2** — Blazor component library
- **MSTest 4.0.1** + **Moq 4.20.72** — unit testing

---

## Architecture Patterns

### Layered (N-Tier) Architecture

```
LanyardApp (Presentation)
    ↓ calls
LanyardServices (Application / Business Logic)
    ↓ calls
LanyardData (Infrastructure / Data Access)
    ↓
PostgreSQL
```

`LanyardClient` is a separate executable that connects via SignalR and HTTP.

### Service Pattern with Interface Abstraction

Every domain has an interface and a concrete implementation registered via DI:

```csharp
// Interface in LanyardServices/Services/
public interface IClientService { ... }

// Implementation
public class ClientService : IClientService { ... }

// Registration in LanyardApp/Program.cs
builder.Services.AddScoped<IClientService, ClientService>();
```

All services use `IDbContextFactory<ApplicationDbContext>` (not `IDbContext` directly) to support concurrent async access safely.

### Result\<T\> Pattern

All service operations return `Result<T>` instead of throwing exceptions:

```csharp
// LanyardData/DTO/Result.cs
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

Use `Result<T>` as the return type for any service method that can logically fail. Do not use exceptions for expected failure cases.

### DI Lifetime Rules

| Lifetime | Used For |
|---|---|
| `Singleton` | `MusicPlayerService`, `LaserGameStatusStore`, `AppInfo` — stateful, shared across all requests |
| `Scoped` | All business services (`IClientService`, `IMusicService`, etc.), `DragStateService`, `AuthenticationStateProvider` |
| `Transient` | Not used |

### SignalR Hub

**Server:** `LanyardServices/SignalR/SignalRControlHub.cs`
- Inherits `Hub`, implements `ISignalRProjectionControlHub`
- Endpoint: `/websocket`
- Tracks connections in `ConcurrentDictionary<string, bool> _connections`
- Clients join the `"Music"` group on connect
- `OnConnectedAsync`: validates `clientId` query param, upserts client in DB, sends current music settings and projection programs to new client
- `OnDisconnectedAsync`: removes from group and cleans up laser game status store

**Client → Server methods (incoming):**
- `PlaybackStateChanged(PlaybackState state)` — client reports its playback state
- `CurrentPlayingSongChanged(Guid song)` — client reports the song it started playing
- `UpdateAvailableScreens(IEnumerable<ClientAvailableScreenDTO> screens)` — client reports its connected displays
- `UpdateLaserGameStatus(LaserGameStatusDTO status)` — client reports parsed laser game state

**Server → Client methods (outgoing):**
- `Load(Guid songId)` — broadcast to `"Music"` group
- `Play()`, `Pause()`, `Stop()` — broadcast to `"Music"` group
- `ReceiveMusicSettings(ClientMusicSettingsDTO)` — sent to caller only
- `ReceiveProjectionPrograms(IEnumerable<ClientProjectionSettingsDTO>)` — sent to specific connection ID

### In-Memory State Stores

`MusicPlayerService` (singleton) maintains per-client playback state:

```csharp
// Private nested type per client
private sealed class ClientMusicState
{
    PlaybackState CurrentState;
    Song CurrentSong;
    Playlist CurrentPlaylist;
    List<Song> Queue;
    int QueueIndex;
    bool IsShuffleEnabled;
    bool IsRepeatEnabled;
    double LastKnownPositionSeconds;
    DateTime LastPositionUpdateUtc;
}
```

`LaserGameStatusStore` (singleton) holds `ConcurrentDictionary<Guid, LaserGameStatusDTO>` — one entry per connected client.

### Song Caching (LanyardClient)

`ISongCacheService` / `SongCacheService` — disk-based LRU cache:
1. Checks local disk for cached file
2. If missing, downloads via `GET {serverUrl}/api/music/audio/{id}` (range-request streaming)
3. Saves to local disk up to `Client.MusicCacheLimitMb` (default 500 MB)
4. Returns local file path for NAudio to open

### Packet Sniffing (LanyardClient)

`PacketSniffer` (SharpPcap) captures UDP broadcast packets from laser game arcade hardware (source IP range `192.168.0.10–11`, `192.168.2.42` → `192.168.0.255`). `GameStateService` parses the binary payload into typed game events. `LaserGameStatePublisher` forwards `LaserGameStatusDTO` to the hub via SignalR.

---

## Data Models

### Naming & Conventions

- **Primary keys:** `Guid Id`
- **Timestamps:** `DateTime CreateDate`, `UpdateDate`, `LastLogin`, `LastUpdateDate`
- **Soft delete:** `bool IsActive` — never physically delete rows
- **Relationships:** explicit `[ForeignKey]` attributes with navigation properties
- **Composite PKs:** `PlaylistSongMember` uses `(SongId, PlaylistId)`
- **Indexes:** added for frequently queried FKs (e.g. `FolderId`, `ParentFolderId`)

### Key Entities (LanyardData/Models/)

**Identity**
- `UserProfile : IdentityUser` — adds `FirstName`, `LastName`, `DateOfBirth`, `GetName()`
- `ApplicationRole : IdentityRole` — adds `CreatedByUserId`, `CreateDate`, `IsActive`
- Seed roles: `Admin`, `Manager`, `Staff`, `CanControlMusic`, `CanClockIn`
- Seed user: `admin@play2day.com` / `ADMIN` with all roles

**Clients**
- `Client` — represents a remote `LanyardClient` machine; stores `MostRecentConnectionId`, `MusicCacheLimitMb`
- `ClientProjectionSettings` — per-client display configuration (program, display index, size, theme)
- `ClientAvailableScreen` — screens reported by the client on connect

**Music**
- `Song` — `Id`, `Name`, `AlbumName`, `FilePath`, `DurationSeconds`
- `Playlist` — owned by a user with soft-delete support
- `PlaylistSongMember` — M2M join between `Playlist` and `Song`

**Projection Programs**
- `ProjectionProgram` → `ProjectionProgramStep` → `ProjectionProgramParameterValue`
- `ProjectionProgramStepTemplate` defines available step types and their parameters (`ProjectionProgramStepTemplateParameter`)

**Dashboard**
- `Dashboard` → `DashboardWidget` — grid-based widget layout; widget config stored as `ConfigJson` (JSON string)

**Files**
- `FileMetadata` — uploaded file metadata; soft-deleted with `IsActive`
- `Folder` — hierarchical self-referencing via `ParentFolderId`

### DTOs

**`Lanyard.Shared/DTO/`** — used by both server and `LanyardClient`:
- `ClientAvailableScreenDTO`, `ClientAvailableAudioDeviceDTO`, `ClientProjectionSettingsDTO`
- `ProjectionProgramDTO`, `ProjectionProgramStepDTO`, `ProjectionProgramParameterValueDTO`
- `PlayerScoreDTO`, `PlayerHitDTO`, `LaserGameStatusDTO`

**`LanyardData/DTO/`** — infrastructure layer only:
- `ClientConnectedDTO`, `ClientConnectedWithCapabilitiesDTO`, `ClientMusicSettingsDTO`
- `SongDTO`, `LoginDTO`
- `Result<T>` — generic operation result wrapper

### Enums (Lanyard.Shared/Enum/)

- `ClientGroup` — `Music(1)`, `PacketSniffer(2)`, `Projector(3)`
- `ProjectionType` — `Webpage(1)`, `CaptureSource(2)`, `StaticText(3)`, `VideoFile(4)`, `ImageFile(5)`
- `Team` — `Red(0)`, `Green(2)`
- `GameStatus` — `NotStarted(14)`, `InGame(15)`, `GetReady(16)`
- `GameMode` — 18 modes (e.g. `StandardSolo(0)`, `Zombie(17)`)
- `SoundSet` — `Male(1)`, `Female(2)`

---

## Code Style

- **Always use explicit types. Never use `var`.**
- Async methods must be suffixed with `Async` (e.g. `GetClientsAsync`, `UpdateClientAsync`)
- Private fields use `_camelCase` prefix
- Interfaces named `I{ClassName}` (e.g. `IClientService`)
- All `async` operations go through `IDbContextFactory<ApplicationDbContext>` — never inject `ApplicationDbContext` directly into long-lived services
- Use `Result<T>` for service return values that can succeed or fail — do not throw for expected failures
- Use `ILogger<T>` for logging; log errors with `_logger.LogError()`
- `#nullable enable` is on across all projects
- Implicit usings are enabled — no need to explicitly add common `using` statements

---

## Configuration

### Server (`LanyardApp/appsettings.json`)

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=...;Port=5432;Database=lanyarddb;Username=...;Password=..."
  },
  "Jwt": {
    "Key": "...",
    "Issuer": "Lanyard.App",
    "Audience": "Lanyard.App",
    "ExpiryInMinutes": 60
  }
}
```

### Client (`LanyardClient`)

- `SIGNALR_SERVER_URL` — required env var; URL of the SignalR hub
- Client ID is auto-generated on first run and persisted to `~/.lanyardClient/client-id.txt`

### Feature Toggles (Program.cs)

- `DetailedErrors` — enabled in Development only
- `MigrationsEndpoint` — enabled in Development only
- `IsDevelopment()` gates used throughout `LanyardApp/Program.cs`

---

## Testing

- Framework: MSTest with Moq and EF Core InMemory provider
- Test files mirror source: `LanyardTests/Services/` mirrors `LanyardServices/Services/`
- Coverage: `ClientService`, `DashboardService`, `FileService`
- Use `IDbContextFactory<ApplicationDbContext>` backed by InMemory provider in tests — do not mock `DbContext` directly

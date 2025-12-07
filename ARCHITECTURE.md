# Music Player Architecture

## Overview
The music player has been refactored into a clean three-layer architecture:

1. **MusicPlayer** (Singleton) - Pure audio playback logic
2. **MusicPlayerService** (Scoped) - Business logic and orchestration
3. **MusicRepository** (Scoped) - Database operations

## Architecture

```
???????????????????????????????????????????
?         Blazor Components               ?
?    (Inject MusicPlayerService)          ?
???????????????????????????????????????????
               ?
               ?
???????????????????????????????????????????
?      MusicPlayerService (Scoped)        ?
?  - Coordinates between Player & Repo    ?
?  - Business logic (verify metadata)     ?
?  - Async operations                     ?
???????????????????????????????????????????
       ?                          ?
       ?                          ?
????????????????????    ????????????????????
?  MusicPlayer     ?    ?  MusicRepository ?
?  (Singleton)     ?    ?  (Scoped)        ?
?                  ?    ?                  ?
?  - Audio player  ?    ?  - DbContext     ?
?  - Queue mgmt    ?    ?  - DB queries    ?
?  - Playback      ?    ?  - Updates       ?
?  - NO database   ?    ?                  ?
????????????????????    ????????????????????
```

## Components

### 1. MusicPlayer (Singleton)
**Location:** `LanyardAPI/Services/MusicPlayer.cs`

**Responsibility:** Pure audio playback and queue management

**Key Features:**
- Manages NAudio WaveOutEvent and AudioFileReader
- Maintains song queue and current index
- Exposes playback controls (Play, Pause, Stop, Seek)
- Queue navigation (Next, Previous)
- Events for state changes
- NO database dependencies

**Lifetime:** Singleton - shared across all users/requests

### 2. MusicPlayerService (Scoped)
**Location:** `LanyardAPI/Controllers/MusicPlayerController.cs`

**Responsibility:** Business logic and orchestration layer

**Key Features:**
- Delegates audio operations to MusicPlayer
- Delegates database operations to MusicRepository
- Handles async operations
- Verifies song metadata before playing
- Coordinates playlist loading
- Scans local music files

**Lifetime:** Scoped - new instance per request/circuit

**Usage in Blazor:**
```razor
@inject MusicPlayerService _player

await _player.Play(song);
await _player.LoadPlaylist(playlistId);
```

### 3. MusicRepository (Scoped)
**Location:** `LanyardAPI/Services/MusicRepository.cs`

**Responsibility:** All database operations

**Key Features:**
- Direct ApplicationDbContext injection
- Update song metadata
- Fetch playlists
- Get playlist songs (randomized)
- Query existing song paths

**Lifetime:** Scoped - new instance per request/circuit

## Benefits

? **Separation of Concerns**
   - Audio logic isolated from database logic
   - Business logic separate from infrastructure

? **Proper Dependency Injection**
   - No lifetime mismatches
   - DbContext only in scoped services
   - Singleton player remains stateful

? **Testability**
   - Each layer can be tested independently
   - Easy to mock dependencies

? **Maintainability**
   - Clear responsibilities
   - Easy to locate and modify functionality

? **Thread Safety**
   - Singleton player manages global state
   - Scoped services handle per-request logic

## Service Registration

```csharp
// Program.cs
builder.Services.AddSingleton<MusicPlayer>();
builder.Services.AddScoped<MusicPlayerService>();
builder.Services.AddScoped<MusicRepository>();
```

## Migration Notes

- Components now inject `MusicPlayerService` instead of `MusicPlayer`
- All database calls removed from player logic
- Player state is now truly global (singleton)
- Service methods remain async-compatible

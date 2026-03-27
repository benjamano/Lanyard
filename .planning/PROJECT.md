# LanyardApp — Automation Rules Engine

## What This Is

An extensible automation rules engine built on top of the existing LanyardApp system. Staff configure rules that say "when this game client reaches this state, execute these actions" — starting with music control (unpause/pause on a specific client) and designed to support additional action types (DMX lighting, etc.) without rearchitecting.

## Core Value

When a laser game starts or ends, the right things happen automatically — no staff intervention required.

## Requirements

### Validated

- ✓ Real-time laser game status tracking per client via SignalR — existing
- ✓ Per-client music play/pause/stop control via SignalR — existing
- ✓ Multi-client dashboard for managing clients — existing
- ✓ `LaserGameStatusStore` (singleton) holds live game state per client — existing
- ✓ `MusicPlayerService` (singleton) manages per-client playback state — existing

### Active

- [ ] Staff can create automation rules with a game client trigger (specific client + GameStatus transition) and one or more actions
- [ ] Staff can add a "Music Control" action to a rule (target client + play or pause)
- [ ] Staff can edit and delete rules from the dashboard
- [ ] Staff can enable/disable the automation engine globally from the dashboard
- [ ] When a monitored game client transitions to `InGame`, matching rules fire their actions
- [ ] When a monitored game client transitions to `NotStarted`, matching rules fire their actions
- [ ] Rules engine architecture supports additional action types (e.g. DMX) without structural changes

### Out of Scope

- DMX action type — architecture supports it, implementation deferred to future milestone
- `GetReady` state as a trigger — deferred, not needed for v1
- Delayed pause after game end — deferred, immediate pause is sufficient for v1
- Global "any client" trigger scope — rules are always scoped to a specific game client

## Context

LanyardApp is a Blazor Server + SignalR system for managing remote arcade/laser tag client machines. Clients report their laser game status (via packet sniffing) and receive music control commands. The existing `LaserGameStatusStore` and `MusicPlayerService` singletons already hold the live state needed to detect transitions and fire actions. The new automation engine sits as a layer that watches for state changes and dispatches configured actions.

The engine should be stored in the database so rules persist across restarts, and evaluated server-side whenever the hub receives a `UpdateLaserGameStatus` call.

## Constraints

- **Tech stack**: .NET 10, Blazor Server, EF Core + PostgreSQL, SignalR — must stay within existing stack
- **Architecture**: Follow existing service pattern (interface + implementation, `Result<T>`, `IDbContextFactory`)
- **Extensibility**: Action type must be polymorphic/discriminated so new action types (DMX, etc.) can be added to the DB schema and engine without breaking existing rules

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Per-trigger rules (not per-client mapping) | More flexible — one game event can fan out to many actions of different types | — Pending |
| Specific client trigger (not any client) | Venue may have independent rooms; rules should be room-scoped | — Pending |
| Music-only action type in v1 | DMX integration doesn't exist yet; build the engine, not the integration | — Pending |
| Global enable/disable toggle | Staff need a quick kill switch without deleting rules | — Pending |

---
*Last updated: 2026-03-27 after initialization*

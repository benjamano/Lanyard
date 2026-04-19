# LanyardApp — Automation Rules Engine

## What This Is

An extensible automation rules engine built on top of the existing LanyardApp system. Staff configure rules that say "when this game client reaches this state, execute these actions" — starting with music control (play/pause on a specific client) and designed to support additional action types (DMX lighting, etc.) without rearchitecting. Rules, actions, and execution logs persist in PostgreSQL and are fully managed from the Blazor staff dashboard.

## Core Value

When a laser game starts or ends, the right things happen automatically — no staff intervention required.

## Requirements

### Validated

- ✓ Real-time laser game status tracking per client via SignalR — existing
- ✓ Per-client music play/pause/stop control via SignalR — existing
- ✓ Multi-client dashboard for managing clients — existing
- ✓ `LaserGameStatusStore` (singleton) holds live game state per client — existing
- ✓ `MusicPlayerService` (singleton) manages per-client playback state — existing
- ✓ Staff can create automation rules with a game client trigger (specific client + GameStatus transition) and one or more actions — v1.0
- ✓ Staff can add a "Music Control" action to a rule (target client + play or pause) — v1.0
- ✓ Staff can edit and delete rules from the dashboard — v1.0
- ✓ Staff can enable/disable the automation engine globally from the dashboard — v1.0
- ✓ When a monitored game client transitions to `InGame`, matching rules fire their actions — v1.0
- ✓ When a monitored game client transitions to `NotStarted`, matching rules fire their actions — v1.0
- ✓ Each rule execution is recorded with timestamp, rule name snapshot, trigger event, and per-action outcome — v1.0
- ✓ Staff can view the last 50 execution log entries with per-action breakdown — v1.0
- ✓ Rules engine architecture supports additional action types (e.g. DMX) without structural changes — v1.0

### Active

*(None — all v1.0 requirements shipped)*

### Out of Scope

- DMX action type — architecture supports it (IActionExecutor pattern), implementation deferred to future milestone
- `GetReady` state as a trigger — deferred, not needed for v1
- Delayed pause after game end — deferred, immediate pause is sufficient for v1
- Global "any client" trigger scope — rules are always scoped to a specific game client
- Per-rule enable/disable toggle — global engine toggle sufficient for v1

## Context

LanyardApp is a Blazor Server + SignalR system for managing remote arcade/laser tag client machines. Clients report their laser game status (via packet sniffing) and receive music control commands.

**v1.0 shipped** (2026-04-05): Full automation engine from schema to Blazor UI. 4 phases, 9 plans, ~2,565 LOC of automation code added to the codebase.

**Architecture delivered:**
- `AutomationEngineService` (singleton) — unbounded `Channel<GameStatusTransitionEvent>`, per-client `_lastKnownStatus` edge dedup, `IActionExecutor` dispatch, fault-isolated per-action try/catch, execution log writes
- `AutomationEngineHostedService` (`BackgroundService`) — drains channel via `await foreach`, non-blocking to hub
- `MusicControlActionExecutor` — `ParametersJson {"TargetClientId", "Operation"}`, SignalR connection pre-check
- `IAutomationRuleService` / `AutomationRuleService` — scoped CRUD, soft-delete, `InvalidateRuleCache()` on every write
- `IAutomationLogService` / `AutomationLogService` — last-N executions with `ActionExecutions` included
- Blazor `/staff/automation` page — engine toggle (reads AppSettings from DB on init), rules `FluentDataGrid`, `AddEditAutomationRuleDialog` (two-step, FK-safe edit), execution log with `ExecutionLogDetailDialog`

## Constraints

- **Tech stack**: .NET 10, Blazor Server, EF Core + PostgreSQL, SignalR — stay within existing stack
- **Architecture**: Follow existing service pattern (interface + implementation, `Result<T>`, `IDbContextFactory`)
- **Extensibility**: Action type is polymorphic via `ActionType` string + `ParametersJson` — new action types (DMX, etc.) only need a new `IActionExecutor` and dialog UI

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Per-trigger rules (not per-client mapping) | More flexible — one game event can fan out to many actions of different types | ✓ Good — used as designed |
| Specific client trigger (not any client) | Venue may have independent rooms; rules should be room-scoped | ✓ Good |
| Music-only action type in v1 | DMX integration doesn't exist yet; build the engine, not the integration | ✓ Good — IActionExecutor pattern ready for DMX |
| Global enable/disable toggle | Staff need a quick kill switch without deleting rules | ✓ Good |
| ConfigJson/string ActionType + ParametersJson for action config | Matches DashboardWidget precedent; no queries by action type needed | ✓ Good — extensible without EF Core TPH complexity |
| Channel<T> + IHostedService for hub decoupling | Hub-blocking risk under DB load is real | ✓ Good — TryWrite is synchronous, hub never awaits engine |
| Engine maintains own `_lastKnownStatus` map (not modifying LaserGameStatusStore) | Keeps feature self-contained | ✓ Good |
| AppSetting row for engine toggle persistence | Generic KV store; toggle is a one-off setting, not a domain model | ✓ Good |
| DB read for engine toggle initial state (not in-memory IsEnabled) | IsEnabled starts false until first game event; misleading after restart | ✓ Fixed in Phase 4 gap closure |
| AutomationRuleId FK assignment before UpdateRuleAsync on edit | EF Core disconnected graph doesn't infer FK from parent Update | ✓ Fixed in Phase 4 gap closure |

---
*Last updated: 2026-04-05 after v1.0 milestone*

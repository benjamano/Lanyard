# Requirements: LanyardApp — Automation Rules Engine

**Defined:** 2026-03-27
**Core Value:** When a laser game starts or ends, the right things happen automatically — no staff intervention required.

## v1 Requirements

### Rules Management

- [x] **RULE-01**: Staff can create an automation rule with a name, a specific trigger client (game-monitoring client), and a trigger event (InGame or NotStarted)
- [x] **RULE-02**: Staff can add one or more Music Control actions to a rule, each specifying a target music client and an operation (play or pause)
- [x] **RULE-03**: Staff can edit an existing rule's name, trigger client, trigger event, and actions
- [x] **RULE-04**: Staff can delete a rule
- [x] **RULE-05**: Staff can enable or disable the entire automation engine from the dashboard (global kill switch)

### Engine Behavior

- [ ] **ENG-01**: Rules fire only on game status *transition* (edge-triggered) — not on every status update call
- [ ] **ENG-02**: When a monitored game client transitions to InGame, all matching rules execute their actions
- [ ] **ENG-03**: When a monitored game client transitions to NotStarted, all matching rules execute their actions
- [x] **ENG-04**: Action execution is fault-isolated per-action — one action failing does not abort remaining actions in the same rule
- [x] **ENG-05**: Rule evaluation and action dispatch do not block the SignalR hub method (async, non-blocking)

### Execution Logging

- [x] **LOG-01**: Each rule execution is recorded with: timestamp, rule name, trigger event, and per-action outcome (success or failure with reason)
- [x] **LOG-02**: Staff can view a list of recent rule execution log entries from the dashboard

## v2 Requirements

### Rules Management

- **RULE-06**: Individual rules have their own enable/disable toggle (independent of global engine toggle)

### Engine Behavior

- **ENG-06**: GetReady game status can be used as a trigger event
- **ENG-07**: Actions support a configurable delay offset (e.g. pause music 3 seconds after game ends)

### Actions

- **ACT-01**: DMX Control action type: target DMX client + command (set color, scene, etc.)

## Out of Scope

| Feature | Reason |
|---------|--------|
| DMX action type | Integration doesn't exist yet — architecture supports it, implementation deferred |
| GetReady trigger | Not needed for v1; deferred |
| Delayed pause | Deferred; immediate is sufficient for v1 |
| Per-rule enable/disable | Global toggle sufficient for v1; adds UI complexity |
| Visual flow editor | Anti-feature — internal tooling doesn't need it; typed forms cover all cases |
| Multi-client "any game" trigger | Rules always scoped to a specific client — room isolation requirement |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| RULE-01 | Phase 3 | Complete |
| RULE-02 | Phase 3 | Complete |
| RULE-03 | Phase 3 | Complete |
| RULE-04 | Phase 3 | Complete |
| RULE-05 | Phase 3 | Complete |
| ENG-01 | Phase 2 | Pending |
| ENG-02 | Phase 2 | Pending |
| ENG-03 | Phase 2 | Pending |
| ENG-04 | Phase 2 | Complete |
| ENG-05 | Phase 2 | Complete |
| LOG-01 | Phase 2 | Complete |
| LOG-02 | Phase 3 | Complete |

**Coverage:**
- v1 requirements: 12 total
- Mapped to phases: 12
- Unmapped: 0 ✓

---
*Requirements defined: 2026-03-27*
*Last updated: 2026-03-28 — ENG-05 marked complete after 02-04 hub wiring and DI registration*

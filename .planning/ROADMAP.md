# Roadmap: LanyardApp — Automation Rules Engine

## Overview

Three phases deliver a persistent, real-time automation rules engine on top of the existing LanyardApp system. Phase 1 lays the database foundation that every other component depends on. Phase 2 builds the engine itself — the CRUD service, background processor, hub integration, and execution logging — so that rules actually fire and music responds to game state. Phase 3 exposes everything to staff through Blazor management pages so they can configure, monitor, and kill-switch the engine without touching code.

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [x] **Phase 1: Data Foundation** - EF Core entities, migration, and DbContext registration for AutomationRule and AutomationRuleAction (completed 2026-03-27)
- [x] **Phase 2: Engine Core** - AutomationRuleService (CRUD), AutomationEngineService (singleton), IActionExecutor pattern, MusicControlActionExecutor, Channel<T> + IHostedService background consumer, hub wiring, and execution logging persistence (gap closure in progress) (completed 2026-03-28)
- [ ] **Phase 3: Management UI** - Blazor pages for rule CRUD, global engine toggle, and execution log view

## Phase Details

### Phase 1: Data Foundation
**Goal**: The AutomationRule and AutomationRuleAction tables exist in PostgreSQL with all fields needed to describe a rule, its actions, and their execution history — so that every subsequent component has a schema to build against.
**Depends on**: Nothing (first phase)
**Requirements**: (no v1 requirements are satisfied by schema alone — this phase unblocks Phase 2 and Phase 3)
**Success Criteria** (what must be TRUE):
  1. Running `dotnet ef database update` applies the migration cleanly against a fresh database with no errors
  2. The AutomationRules and AutomationRuleActions tables exist in the database with the correct columns (Guid PKs, ActionType string discriminator, ParametersJson, SortOrder, IsActive, timestamps)
  3. AutomationRuleExecution and AutomationRuleActionExecution log tables exist with FK to AutomationRule
  4. ApplicationDbContext compiles with DbSet registrations for all new entities and the project builds with no errors
**Plans**: 1 plan

Plans:
- [ ] 01-01-PLAN.md — Create AutomationModels.cs (four entity classes), register DbSets + HasIndex in ApplicationDbContext, generate and apply EF Core migration

### Phase 2: Engine Core
**Goal**: When a laser game client transitions to InGame or NotStarted, all matching active automation rules execute their music control actions automatically — non-blocking, fault-isolated, and with every execution recorded to the database.
**Depends on**: Phase 1
**Requirements**: ENG-01, ENG-02, ENG-03, ENG-04, ENG-05, LOG-01
**Success Criteria** (what must be TRUE):
  1. When a laser game client transitions from any state to InGame, all AutomationRules with that client as trigger client and InGame as trigger event execute within seconds, and music on the targeted client(s) plays or pauses as configured
  2. When a laser game client transitions to NotStarted, matching rules execute their actions — music pauses on targeted clients
  3. The same status packet arriving twice in a row for the same GameStatus does not cause rules to fire a second time (edge-triggered, not level-triggered)
  4. If one action's music command fails (e.g. target client disconnected), the remaining actions in the same rule still execute and the failure is recorded
  5. The SignalR hub method returns promptly regardless of how many rules are configured — rule execution happens off the hub thread via a background Channel consumer
  6. Each rule execution creates a log entry in the database recording the rule name, trigger event, timestamp, and per-action outcome (success or error message)
**Plans**: 5 plans

Plans:
- [ ] 02-01-PLAN.md — AppSetting model + EF migration + IAutomationRuleService CRUD + AutomationRuleService (scoped, Result<T>, cache invalidation signaling) + Wave 0 test stubs
- [ ] 02-02-PLAN.md — IActionExecutor interface + MusicControlActionExecutor (ParametersJson, ConnectedIds pre-check, named error messages) + Wave 0 test stubs
- [x] 02-03-PLAN.md — GameStatusTransitionEvent record + AutomationEngineService singleton (unbounded channel, _lastKnownStatus, volatile toggle, fault isolation, execution log) + AutomationEngineHostedService + Wave 0 test stubs
- [x] 02-04-PLAN.md — Hub wiring (EnqueueTransition call in UpdateLaserGameStatus) + DI registrations in Program.cs + full solution build verify
- [ ] 02-05-PLAN.md (gap closure) — Add RuleName snapshot to AutomationRuleExecution; assign rule.Name in ExecuteRuleAsync; generate and apply EF migration (closes LOG-01)

### Phase 3: Management UI
**Goal**: Staff can create, edit, and delete automation rules from the dashboard, toggle the engine on and off with immediate effect, and view recent execution log entries to diagnose what fired and why.
**Depends on**: Phase 2
**Requirements**: RULE-01, RULE-02, RULE-03, RULE-04, RULE-05, LOG-02
**Success Criteria** (what must be TRUE):
  1. Staff can navigate to an Automation Rules page, see all configured rules, create a new rule by picking a trigger client, trigger event (InGame or NotStarted), and adding one or more Music Control actions (target client + play or pause)
  2. Staff can open an existing rule, change any field (name, trigger client, trigger event, actions), save, and see the updated rule reflected immediately in the list
  3. Staff can delete a rule and it no longer appears in the list or fires during engine evaluation
  4. Staff can toggle the global automation engine on or off from the dashboard and the change takes effect immediately — rules stop firing while disabled without requiring a server restart
  5. Staff can view a list of recent rule execution log entries showing which rule fired, when, what triggered it, and whether each action succeeded or failed
**Plans**: 2 plans

Plans:
- [ ] 03-01-PLAN.md — IAutomationLogService + AutomationLogService (scoped) + test stubs + DI registration; Automation.razor (/staff/automation) with FluentToolbar engine toggle + rules FluentDataGrid + create/edit/delete; AddEditAutomationRuleDialog.razor two-step IDialogContentComponent
- [ ] 03-02-PLAN.md — Execution log FluentCard section on Automation.razor (last 50 entries, failures-only filter, row click opens detail); ExecutionLogDetailDialog.razor (read-only per-action breakdown); NavMenu Automation entry (Admin, Manage dropdown)

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Data Foundation | 1/1 | Complete    | 2026-03-27 |
| 2. Engine Core | 5/5 | Complete    | 2026-03-28 |
| 3. Management UI | 0/2 | Not started | - |

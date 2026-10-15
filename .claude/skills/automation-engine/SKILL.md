---
name: automation-engine
description: How AutomationEngineService actually works — why adding a new automation action type requires touching 3 separate places with no compile-time enforcement (and fails silently, not loudly, if you miss one), plus the lazy-init/rule-cache pattern, how only-if conditions are evaluated (venue time, fail closed, tenancy, skip logging) and how rule saves sync child rows. Use whenever adding a new automation action type or condition type, debugging a rule that silently doesn't fire or shows as Skipped, or touching AutomationEngineService/IActionExecutor/AutomationActionTypes/AutomationRuleService.
---

# Automation engine

`AutomationEngineService` is registered `AddSingleton` in `Program.cs`, same pattern (and same reasoning — see the `dmx-scene-engine` skill for why singleton services here must use `IDbContextFactory` rather than an injected `DbContext`) as the DMX scene engine.

## Adding a new automation action type — 3 touchpoints, silent failure if you miss one

1. Add a new constant in `AutomationActionTypes.cs`.
2. Add a new `IActionExecutor` implementation, registered `AddSingleton` in `Program.cs` (alongside the existing ones, e.g. `DmxSceneControlActionExecutor`).
3. Wire it into the UI in `AddEditAutomationRuleDialog.razor` so a user can actually select it when building a rule.

**`AutomationEngineService.ExecuteRuleAsync` does not throw or log an error if an action type has no matching executor — it silently records "Action type not supported" and moves on.** This is a classic silent-miss failure mode: a rule can be created and saved successfully (the constant/type exists), but if step 2 or step 3 was skipped, the rule appears to work from the UI while doing nothing at runtime. If a rule "isn't firing" and there's no exception anywhere, check first whether every action type it references actually has a registered `IActionExecutor` — don't assume the bug is in the rule's trigger/condition logic.

## Lazy init and the rule cache

The enabled flag is read once, lazily, through `EnsureEnabledInitializedAsync`: a `LazyInitializer`-cached `Task<bool>` that every caller awaits (the transition consumer and the idle and scheduled hosted services can all arrive together on startup). A failed read clears the cached task so the next tick retries. The rule cache reloads under a `SemaphoreSlim` when `_ruleCacheDirty` is set; `AutomationRuleService` sets it on every save. (An older version of this note described a blocking `.GetAwaiter().GetResult()` inside a `lock`; that code is gone.)

Transitions still go through **one channel with one consumer** (`AutomationEngineHostedService`), so per-rule state touched only by the consumer needs no locking. Keep it that way, or add locking, if you add another reader.

## Conditions ("only if")

`AutomationRuleCondition` rows (type + `ParametersJson`, soft-deleted with `IsActive`) hang off a rule like actions do. All active conditions must pass (AND); a rule with none behaves exactly as before.

- **Logic lives in two pure places:** `AutomationConditionParameters` (Infrastructure: typed records, parse, `Validate`) and `AutomationConditionEvaluator` (Services: `Evaluate`). The dialog, `AutomationRuleService.ValidateConditions` and the engine all use these, so they can't disagree.
- **Venue time, not server time.** Conditions use `RotaTime.ToVenueLocal`. The production container is UTC, so `DateTime.Now` would be an hour out all summer. Scheduled triggers use venue time too, since `ScheduledTriggerHostedService` passes `RotaTime.ToVenueLocal(DateTime.UtcNow)`.
- **Fail closed.** Unparsable JSON, an unknown type, a deleted kiosk or a kiosk in another company stops the rule (logged as a warning) rather than letting it run.
- **Tenancy.** The engine's context is unfiltered (`ISystemDbContextFactory`), so it checks a condition's kiosk `CompanyId` against the rule's itself. On save, `AutomationRuleService` looks the kiosk up through the tenant-filtered context, so another company's kiosk is "not found".
- **Opening hours** (`DuringOpeningHours`) read `LocationOpeningHours` rows through `OpeningHoursRules.IsOpen`; hours past midnight carry into the next morning, and a location with no hours counts as closed (a normal skip, not misconfigured). The engine loads the named locations and their active hours with the rule cache, leaving out deactivated locations so their conditions fail closed. **Locations aren't tenant-filtered**, so both the engine and `AutomationRuleService` compare the location's `CompanyId` to the rule's explicitly. `LocationOpeningHoursService` calls `InvalidateRuleCache()` on save; anything else that changes what a condition reads must do the same, or rules keep using stale data until the next rule edit.
- **"In game" comes from `ILaserGameStatusStore`**, not `_lastKnownStatus`. The hub clears the store on disconnect, so an offline kiosk is "not in game".
- **Skips are logged** as an `AutomationRuleExecution` with `SkipReason` set, `OverallSuccess = false` and no action rows, and `OnRuleExecuted` isn't raised. Anything reading the log must treat `SkipReason != null` as "not a run": the failures filter, the widget's latest-execution lookup and the scheduled reseed (which only counts `OverallSuccess` rows) all do.
- **A skip doesn't consume an idle stretch or a scheduled day.** Those rules re-check each tick and fire once the conditions hold; the skip itself is logged once per stretch or day. A status-transition edge is one-shot, so a skip consumes it.

## Game-data triggers (TimeRemainingBelow, ScoreThreshold)

These run from a **second channel with its own single consumer** (`GameDataTriggerHostedService` -> `ProcessGameDataAsync`), separate from the status-transition channel.

- **Coalesced.** The kiosk publishes on every score packet and countdown tick. `EnqueueGameData` keeps only the latest `GameSnapshot` per kiosk, and lets at most one `GameDataEvent` per kiosk wait in the channel (`_gameDataPending`). It does nothing at all while `_hasGameDataRules` is false and the cache is current, so venues without these rules pay nothing.
- **Once per game.** `EnqueueTransition` counts games per kiosk (bumped on each move into InGame), and each snapshot is stamped with that number at enqueue time. That makes the order the two channels drain in irrelevant. Per-rule state (`_gameDataFiredForGame`, `_timeRemainingObserved`) belongs to the single consumer only.
- **Only while InGame.** After a game the kiosk zeroes the countdown and keeps republishing the final scores; neither is a crossing.
- **Time remaining** needs a reading above the threshold earlier in the same game, because kiosks report 0 before their first countdown packet.
- **Score** uses `IsScoreThresholdReached` (pure): any player, or a team's total (players with no team don't count), optionally one team.
- A crossing is consumed whether the rule runs, is skipped by a condition, or fails. A moment in a game isn't retried.
- Security Issue 9.1: a rogue kiosk can claim any client ID and fake these. Not addressed.

## Saving rules: don't `ctx.Update` the detached graph

`UpdateRuleAsync` loads the tracked rule and syncs `Actions` and `Conditions` (add new, update changed, soft-delete missing). `ctx.Update(rule)` on the dialog's detached graph used to mark a newly added child (client-generated Guid) as Modified, which failed the save, and overwrote `IsEnabled`. Keep `IsEnabled` changes in `SetRuleEnabledAsync` only.

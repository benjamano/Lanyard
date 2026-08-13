---
name: automation-engine
description: How AutomationEngineService actually works — why adding a new automation action type requires touching 3 separate places with no compile-time enforcement (and fails silently, not loudly, if you miss one), plus the lazy-init caching pattern used for rule evaluation. Use whenever adding a new automation action type, debugging a rule that silently doesn't fire, or touching AutomationEngineService/IActionExecutor/AutomationActionTypes.
---

# Automation engine

`AutomationEngineService` is registered `AddSingleton` in `Program.cs`, same pattern (and same reasoning — see the `dmx-scene-engine` skill for why singleton services here must use `IDbContextFactory` rather than an injected `DbContext`) as the DMX scene engine.

## Adding a new automation action type — 3 touchpoints, silent failure if you miss one

1. Add a new constant in `AutomationActionTypes.cs`.
2. Add a new `IActionExecutor` implementation, registered `AddSingleton` in `Program.cs` (alongside the existing ones, e.g. `DmxSceneControlActionExecutor`).
3. Wire it into the UI in `AddEditAutomationRuleDialog.razor` so a user can actually select it when building a rule.

**`AutomationEngineService.ExecuteRuleAsync` does not throw or log an error if an action type has no matching executor — it silently records "Action type not supported" and moves on.** This is a classic silent-miss failure mode: a rule can be created and saved successfully (the constant/type exists), but if step 2 or step 3 was skipped, the rule appears to work from the UI while doing nothing at runtime. If a rule "isn't firing" and there's no exception anywhere, check first whether every action type it references actually has a registered `IActionExecutor` — don't assume the bug is in the rule's trigger/condition logic.

## Lazy-init cache uses blocking-in-lock — don't refactor this casually

`_isEnabled`/`_ruleCache` are lazily initialized on the *first* `ProcessTransitionAsync` call, using a blocking `.GetAwaiter().GetResult()` inside a `lock`. This looks like an anti-pattern in isolation (blocking inside a lock can deadlock in general), but it works here specifically because the call site is a single-consumer `BackgroundService`-driven channel reader — there's never contention from multiple concurrent callers. If you're refactoring this service to be called from more than one place, this assumption breaks and the blocking-in-lock needs to be replaced with a proper async-safe lazy-init (e.g. `SemaphoreSlim` or `Lazy<Task<T>>`) rather than carried over as-is.

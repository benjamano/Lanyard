---
name: projection-program-engine
description: How projection programs actually run today — ProjectionProgramRunnerService owns the full step/hold/pause/skip loop server-side (a singleton, mirroring DmxSceneRunnerService's pattern), and the WPF kiosk client is now just a thin window host — it opens a WebView2 window pointed at a server-rendered Blazor page (Kiosk.razor/KioskDisplay.razor) which subscribes to the runner's events in-process and re-renders on every step. Use whenever touching projection program playback, ProjectionProgramRunnerService, Kiosk.razor/KioskDisplay.razor, or comparing projection programs' architecture to the DMX scene engine.
---

# Projection program engine

## The kiosk WPF client is a thin window host, not where playback happens

This inverts what an older version of the `dmx-scene-engine` skill said. As of `ProjectionProgramRunnerService`'s introduction, the flow is:

1. `ClientService.TriggerProjectionProgramOnClientAsync` sends a fire-and-forget `TriggerProjectionProgram(programId, displayIndex)` SignalR message over `SignalRControlHub` to the kiosk. This part hasn't changed.
2. `Lanyard.Client`'s `ProjectionProgramController` receives it and calls `ProjectionProgramsService.TriggerTemporaryProjectionProgramAsync`, which just opens a WPF `ProjectionWindow` hosting a WebView2 browser pointed at `{ServerUrl}/kiosk/{clientId}/{projectionProgramId}?display=...&temporary=...`. The client does **not** step through `HoldForMilliseconds` itself anymore — it just awaits a `TaskCompletionSource` until told to close the window (a separate `CloseTemporaryProjectionWindow` SignalR message).
3. The actual stepping happens server-side, inside that Blazor page: `Kiosk.razor`/`KioskDisplay.razor` call `IProjectionProgramRunnerService.StartAsync(...)` and subscribe directly to `OnProgramStepAdvanced`/`OnProgramPauseChanged`/etc. Because the Blazor Server circuit backing that WebView2 page runs in the same server process as the runner, this is a plain in-process event subscription — not a SignalR hop, and not the same `SignalRControlHub` used for the initial trigger/close messages. Each event calls `StateHasChanged()`, which Blazor Server's own circuit pushes to the WebView2 browser.

**Implication**: the runner's step-tracking (`CurrentStepIndex`, pause state, etc.) is not shadow state visible only to the manager's live-control panel or automation rules — it's the actual source of truth driving what's on screen. Pausing, resuming, or skipping via the runner (from `ProjectionProgramLiveControl.razor`, an automation rule, or a dashboard widget) immediately changes what the kiosk displays, the same tick it would for the manager's own view.

## Mirrors DmxSceneRunnerService's pattern, with one real difference left

Both `ProjectionProgramRunnerService` and `DmxSceneRunnerService` are singletons with the step/timing loop server-side (see `dmx-scene-engine` for why singleton + `IDbContextFactory` is required — the same reasoning applies here). The one architectural difference that remains: DMX pushes each channel value to the client over the custom `SignalRControlHub` (`DmxService.UpdateChannelValue` → `ReceiveDmxChannelValue`), because a physical DMX device has no browser to render into. Projection programs render into an actual Blazor page in a WebView2 browser, so they ride on Blazor Server's own circuit for step-level updates instead of a custom hub message per step.

## Where to look before assuming a projection-program feature isn't wired up

Before assuming pause/skip/progress-tracking would need to be built for a new feature, check `IProjectionProgramRunnerService` first — `Pause`/`Resume`/`SkipToNextStep`/`SkipToPreviousStep`/`Stop`/`GetRunningState(s)` already exist, and `ProjectionProgramLiveControl.razor` (manager UI), the `ProjectionStatusWidget` dashboard widget, button-widget actions, and the `ProjectionProgramControl` automation action all consume this same service. Adding another consumer almost never means building new runner functionality — it means wiring UI to what's already there.

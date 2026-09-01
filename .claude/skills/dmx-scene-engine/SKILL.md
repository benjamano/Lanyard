---
name: dmx-scene-engine
description: How DmxService and DmxSceneRunnerService actually work in this repo — why they're registered as singletons with manual locking instead of scoped services, how BPM-synced timing avoids drift, and momentary-scene/restart-by-sceneId semantics. Use whenever touching DMX scene playback, the virtual DMX desk, DmxService/DmxSceneRunnerService, or comparing DMX's architecture to the projection-program system (see the projection-program-engine skill for that side).
---

# DMX scene engine

## Why singleton + lock, not scoped

`DmxService` and `DmxSceneRunnerService` are registered `AddSingleton` in `Program.cs`, not scoped — they hold in-memory `Dictionary<Guid, ...>` state (live channel values, currently-running scenes) across every request and every connected client, protected by a private `object _lock`. This is *why* they must use `IDbContextFactory<ApplicationDbContext>` rather than an injected `DbContext`: a singleton service outlives any single request, so a request-scoped `DbContext` injected into it would be shared across concurrent callers and blow up. If you're adding a new singleton service that needs DB access for periodic/background work, this is the pattern to copy — see the general rule in CLAUDE.md's `IDbContextFactory` section, but the *reason* it's non-negotiable here specifically is the singleton lifetime.

## DMX and projection programs both step server-side now — the difference is how the step reaches the screen

This used to be the single biggest gotcha comparing the two systems, and it's worth getting right since old code comments and an earlier version of this skill both said the opposite: projection programs used to step client-side with no server visibility into progress. **That's no longer true** — `ProjectionProgramRunnerService` now owns the full step/hold/pause/skip loop server-side, the same shape as `DmxSceneRunnerService`. See the `projection-program-engine` skill for the full picture; the short version:

- **DMX scenes**: `DmxSceneRunnerService.RunSceneLoopAsync` runs the step/delay loop on the server and pushes each individual channel value to clients over the custom `SignalRControlHub` (`DmxService.UpdateChannelValue` → client's `ReceiveDmxChannelValue`), because a physical DMX device has no browser to render into.
- **Projection programs**: `ProjectionProgramRunnerService` also runs the step/hold loop on the server, but the kiosk's WPF client no longer renders anything itself — it just hosts a WebView2 window pointed at a server-rendered Blazor page (`Kiosk.razor`/`KioskDisplay.razor`), which subscribes to the runner's step/pause events as a plain in-process event (same process, no SignalR hop) and calls `StateHasChanged()` on every step. Blazor Server's own circuit pushes that to the WebView2 browser.

**Don't assume DMX-style features (pause, skip, progress tracking) are missing from projection programs** — they already exist in `ProjectionProgramRunnerService`; see `projection-program-engine` for what's there before building anything new. The one real asymmetry left is the render path: DMX pushes raw channel values over a custom hub because there's no browser on the other end; projection programs push into an actual Blazor page instead.

## BPM-synced timing recomputes every step, on purpose

`DmxSceneRunnerService` re-derives each step's delay from `IBeatClockService.GetDelayUntilNextStepAsync` (current live playback position + a bounded correction) on every step, rather than accumulating a running total from a fixed duration. This is deliberate — it's heavily commented in the source but not documented outside it. **If you "simplify" this to a running-total accumulator, you will reintroduce timing drift** relative to the actual music playback position; that's the exact bug this design avoids.

## Momentary scenes and restart-by-`sceneId` semantics

- Momentary scenes force their channels back to 0 in a `finally` block regardless of how playback ends (natural completion, cancellation, error) — so cleanup always happens.
- Scenes are keyed by `sceneId`, not a per-run instance id. Starting an already-running scene **cancels and replaces** the existing run rather than running two instances — meaning two different clients/requests can never run the same scene concurrently.
- Steps are snapshotted at the start of a run. Editing a scene's steps in the DB mid-playback doesn't affect the currently-running loop — changes only apply the next time the scene is (re)started.

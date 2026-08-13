---
name: dmx-scene-engine
description: How DmxService and DmxSceneRunnerService actually work in this repo — why they're registered as singletons with manual locking instead of scoped services, why DMX scenes step server-side over SignalR (unlike projection programs, which step client-side on the kiosk), how BPM-synced timing avoids drift, and momentary-scene/restart-by-sceneId semantics. Use whenever touching DMX scene playback, the virtual DMX desk, DmxService/DmxSceneRunnerService, or comparing DMX's architecture to the projection-program system.
---

# DMX scene engine

## Why singleton + lock, not scoped

`DmxService` and `DmxSceneRunnerService` are registered `AddSingleton` in `Program.cs`, not scoped — they hold in-memory `Dictionary<Guid, ...>` state (live channel values, currently-running scenes) across every request and every connected client, protected by a private `object _lock`. This is *why* they must use `IDbContextFactory<ApplicationDbContext>` rather than an injected `DbContext`: a singleton service outlives any single request, so a request-scoped `DbContext` injected into it would be shared across concurrent callers and blow up. If you're adding a new singleton service that needs DB access for periodic/background work, this is the pattern to copy — see the general rule in CLAUDE.md's `IDbContextFactory` section, but the *reason* it's non-negotiable here specifically is the singleton lifetime.

## DMX steps server-side; projection programs step client-side — don't assume symmetry

This is the single most important thing to know before extending either system, because they look like siblings but aren't:

- **DMX scenes**: `DmxSceneRunnerService.RunSceneLoopAsync` runs the *entire* step/delay loop on the **server** — one `Task.Run` per running scene, `Task.Delay` as the scheduler — and pushes each individual channel value to clients over SignalR (`DmxService.UpdateChannelValue` → client's `ReceiveDmxChannelValue`). The server always knows exactly which step a scene is on.
- **Projection programs**: the opposite. `ClientService.TriggerProjectionProgramOnClientAsync` sends one fire-and-forget SignalR message with the whole program id, and `Lanyard.Client` (the kiosk app) steps through `HoldForMilliseconds` itself, locally. The server has no loop, no cancellation token, and no "currently on step N" state for projection programs — once triggered, it has no visibility into playback progress.

**Implication**: if you're asked to add DMX-style features (progress events, pausing mid-run, live-editing a running sequence) to projection programs, that server-side control simply doesn't exist yet and would need to be built — don't assume it's already there just because DMX has it. Conversely, don't "simplify" DMX scenes toward the projection-program model without realizing you'd be giving up server-side control that features may depend on.

## BPM-synced timing recomputes every step, on purpose

`DmxSceneRunnerService` re-derives each step's delay from `IBeatClockService.GetDelayUntilNextStepAsync` (current live playback position + a bounded correction) on every step, rather than accumulating a running total from a fixed duration. This is deliberate — it's heavily commented in the source but not documented outside it. **If you "simplify" this to a running-total accumulator, you will reintroduce timing drift** relative to the actual music playback position; that's the exact bug this design avoids.

## Momentary scenes and restart-by-`sceneId` semantics

- Momentary scenes force their channels back to 0 in a `finally` block regardless of how playback ends (natural completion, cancellation, error) — so cleanup always happens.
- Scenes are keyed by `sceneId`, not a per-run instance id. Starting an already-running scene **cancels and replaces** the existing run rather than running two instances — meaning two different clients/requests can never run the same scene concurrently.
- Steps are snapshotted at the start of a run. Editing a scene's steps in the DB mid-playback doesn't affect the currently-running loop — changes only apply the next time the scene is (re)started.

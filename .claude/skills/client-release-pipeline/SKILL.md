---
name: client-release-pipeline
description: How Lanyard.Client's automated release/auto-update pipeline actually works — release.yml packages and publishes whatever <Version> is currently in Lanyard.Client.csproj on every push to main that touches client-related paths, with no automatic check that the version increased (unlike Lanyard.App, which has a PR-time version-bump gate). A forgotten bump fails the release job, not silently skips it. Use whenever changing anything under src/Lanyard.Client/**, src/Lanyard.Shared/**, src/Lanyard.Infrastructure/**, src/Lanyard.Tests/Client/**, or src/Lanyard.Client.Watchdog/**, or when debugging why a release didn't go out (or why one failed) after merging client changes.
---

# Lanyard.Client release pipeline

`Lanyard.Client` ships via an automated GitHub Actions release, not a manual publish. Understanding this matters any time you touch client code, because it changes what "merge to main" actually does.

## Trigger

`.github/workflows/release.yml` runs on every push to `main` that touches any of:
- `src/Lanyard.Client/**`
- `src/Lanyard.Shared/**`
- `src/Lanyard.Infrastructure/**`
- `src/Lanyard.Tests/Client/**`
- `src/Lanyard.Client.Watchdog/**`

## What it does — and the important gap

The workflow does **not** compare the new version against the previous release. It:
1. Reads whatever `<Version>` is currently set in `src/Lanyard.Client/Lanyard.Client.csproj`.
2. `dotnet publish`es a self-contained `win-x64` build.
3. Packages it with Velopack (`vpk pack --packVersion <that version>`).
4. Tags the commit `LanyardClient-<version>` and pushes the tag.
5. Uploads and publishes a GitHub Release via `vpk upload github ... --publish`.

There is **no CI gate forcing the client's version to increase**. This is easy to assume exists because the *server* app has exactly that gate: `.github/workflows/ci.yml`'s "Require Lanyard.App version bump" step, which runs on every PR into `main` and fails if `<Version>` in `src/Lanyard.Server/LanyardApp/Lanyard.App.csproj` didn't go up. That check only covers `Lanyard.App.csproj` — it says nothing about `Lanyard.Client.csproj`.

**Consequence of forgetting to bump the client version**: the release job doesn't quietly skip. It reaches "Create and push tag" and fails there, because `LanyardClient-<version>` already exists as a tag from the last release. So a client change that lands on `main` without a version bump produces a failed Actions run, not a silent no-op — check the Release workflow run if a client change was expected to ship and doesn't seem to have.

## Before merging any client change to `main`

Bump `<Version>` in `src/Lanyard.Client/Lanyard.Client.csproj`. There's nothing automated stopping you from forgetting — this is a manual discipline, not a CI-enforced one, unlike the server.

## How existing kiosks pick up a new release

`src/Lanyard.Client/AutoUpdate/AutoUpdate.cs`'s `CheckForUpdatesAsync()` runs on every client startup where `ASPNETCORE_ENVIRONMENT != Development` (called from `Program.cs` before the app does anything else). It queries the same GitHub Releases feed via Velopack's `GithubSource` (`https://github.com/benjamano/Lanyard`), and if a newer version is published, downloads it and calls `ApplyUpdatesAndRestart`.

This means a new release reaches a deployed kiosk **the next time that kiosk's client process starts** — not live. That happens via:
- A normal reboot/manual restart.
- `Lanyard.Client.Watchdog` restarting the client after a crash.
- A scheduled restart via `RestartScheduleController` (server-pushed restart schedule).

There's no push-based "update now" mechanism — a kiosk running continuously with no restart trigger will keep running the old version until something restarts its process.

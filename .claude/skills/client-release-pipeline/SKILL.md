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

## How kiosks pick up a new release

From client 1.0.41, kiosks no longer take the newest GitHub release themselves. On startup (and when an admin presses **Update now** in the client's Updates tab) `AutoUpdate` asks the Lanyard server which version it should run:

1. `App.xaml.cs` loads config and the client ID, then calls `AutoUpdate.CheckAndApplyAtStartupAsync(clientId)` (skipped when `ASPNETCORE_ENVIRONMENT=Development`).
2. `LanyardServerUpdateSource` (a Velopack `IUpdateSource`) calls `GET api/client-updates/{clientId}/feed` with the `X-Lanyard-Client-Secret` header.
3. The server (`ClientUpdatePolicyService`) resolves the client's `UpdateRing` - **Stable** (newest non-prerelease or promoted release), **Canary** (newest release of any kind) or **Pinned** (`PinnedClientVersion`) - against the `ClientReleases` table, which `ClientReleaseCatalogService` mirrors from the GitHub Releases API every 5 minutes. Withdrawn releases are never a target.
4. The response is that one release's `releases.win.json` entry (full package only, `FileName` untouched because Velopack uses it as the local path) plus a `FileName -> GitHub download URL` map. `AllowVersionDowngrade = true`, so a pin to an older version, or withdrawing the newest one, moves the kiosk back.
5. Any failure (server down, GitHub down, no target) leaves the kiosk on its current version. It never falls back to taking the latest GitHub release.

Kiosks older than 1.0.41 still use the old path (Velopack `GithubSource`, newest non-prerelease release) and know nothing about rings. They also don't report their version, so the Clients page shows them as "Unknown".

A release still reaches a kiosk only when its client process starts, or on **Update now** (`CheckForUpdates` hub client-result, `ClientHubEvents.CheckForUpdates`), which answers once the feed is fetched and then downloads and restarts in the background.

Known, not handled here: Velopack's apply step restarts `Lanyard.Client.exe` directly, so the Watchdog may not be supervising the client again until the next logon.

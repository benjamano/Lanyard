# Server CI Gate — Design

## Context

Railway auto-deploys the server app (`LanyardApp`/`LanyardAPI`, Dockerized) on every push to `main`. There is currently no automated build/test verification anywhere in the pipeline for the server — the only existing GitHub Actions workflow, `.github/workflows/release.yml`, is unrelated: it packages and releases the WPF kiosk client (`Lanyard.Client`) via Velopack, triggered only on changes under `src/Lanyard.Client/**` etc.

This means broken code can reach `main` — and therefore Railway production — without ever having been built or tested by CI.

## Goal

Add a CI gate that builds and tests the server app on every push to `main` and every PR into `main`. This workflow does **not** touch deployment — Railway's git-triggered auto-deploy stays exactly as-is. This is purely a red/green signal, made enforceable via branch protection.

## Design

New workflow file: `.github/workflows/ci.yml`, name `CI`.

### Triggers

```yaml
on:
  push:
    branches: [main]
  pull_request:
    branches: [main]
```

### Job 1 — `build-and-test`

Runs on `windows-latest`. Windows is required (not just inherited from `release.yml`'s convention) because `LanyardApp.sln` includes the WPF `Lanyard.Client` project, which only builds on Windows.

Steps:
1. `actions/checkout@v4`
2. `actions/setup-dotnet@v4`, `dotnet-version: '10.x'` (NuGet caching is not enabled — there are no `packages.lock.json` files in the repo for `cache: true` to key on)
3. `dotnet restore LanyardApp.CI.slnf`
4. `dotnet build LanyardApp.CI.slnf -c Release --no-restore`
5. `dotnet test src/Lanyard.Tests/Lanyard.Tests.csproj -c Release --no-build`

`LanyardApp.sln` includes `Lanyard.Reach`, a MAUI app whose `TargetFrameworks` includes `net10.0-maccatalyst`, which only builds on macOS. `LanyardApp.CI.slnf` is a solution filter that includes every project in `LanyardApp.sln` except `Lanyard.Reach.csproj`, so `windows-latest` can build everything else. This was discovered when the workflow's first version was merged and then failed on its first real run — not something the original design could have anticipated from static analysis alone.

### Job 2 — `docker-build`

Runs on `ubuntu-latest`, independent of Job 1 (no `needs:`). Validates that the artifact Railway actually builds still builds. Runs on Linux because the Dockerfile's base images (`mcr.microsoft.com/dotnet/sdk:10.0`, `mcr.microsoft.com/dotnet/aspnet:10.0`) are Linux images, and the Dockerfile only touches server-side projects (`Lanyard.Shared`, `Lanyard.Infrastructure`, `LanyardServices`, `LanyardAPI`, `LanyardApp`) — no WPF dependency, so Windows isn't needed here.

Steps:
1. `actions/checkout@v4`
2. `docker build -f Dockerfile .`

### Error handling

No custom retry/fallback logic. Any step failing fails its job — this is a pass/fail gate by design, not a resilient pipeline.

### Branch protection

Configure `main`'s branch protection to require both `build-and-test` and `docker-build` as required status checks before a PR can merge. Without this, the workflow runs and reports status but doesn't block anything.

## Out of scope

- Does not change how Railway deploys (still git-triggered auto-deploy on push to `main`).
- Does not add versioning/tagging/GitHub Releases for the server app.
- Does not gate the Railway deploy itself on CI passing (push-to-main still deploys immediately regardless of CI outcome — only PR merges are gated, via branch protection).

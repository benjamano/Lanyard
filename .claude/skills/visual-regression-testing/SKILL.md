---
name: visual-regression-testing
description: Automated, CI-enforced pixel-diff testing of the Blazor UI under tests/visual-regression/ (a TypeScript @playwright/test project) plus src/Lanyard.VisualTestFixtures/ (a .NET console utility that seeds deterministic fixture data). Use whenever adding a new screen to the suite, regenerating baseline PNGs after an intentional UI change, debugging a visual-regression CI failure, or wondering why this isn't a .NET/MSTest project like the rest of the test suite.
---

# Visual regression testing

Turns the manual "screenshot it and eyeball the PR" loop (see the `verify` skill) into a checked-in suite that runs in CI and fails the build on a real pixel diff, reusing `.claude/scripts/publish-screenshots.sh` to post failing before/after/diff images straight onto the PR.

## Why TypeScript, not .NET/MSTest

Every other test project in this repo (`src/Lanyard.Tests`) is .NET/MSTest, and `src/Lanyard.VisualTestFixtures` next to this suite is `.NET` too — so a `.NET`/`Microsoft.Playwright.MSTest` visual suite looks like the obvious choice. It doesn't work: **`Microsoft.Playwright` for .NET has no built-in screenshot-comparison assertion.** This was verified directly by reflecting over the installed `Microsoft.Playwright` 1.62.0 assembly (the latest on NuGet at the time) — there is no `ToHaveScreenshotAsync` anywhere in it, on `IPageAssertions` or otherwise. `expect(page).toHaveScreenshot()` — baseline storage, `MaxDiffPixelRatio`, animation-freezing — is a `@playwright/test` (JS/TS) feature that was never ported to .NET. Building an equivalent by hand (raw `Page.ScreenshotAsync()` + a manual image-diff library) would mean maintaining our own version of a solved problem.

So the suite is deliberately split by responsibility:
- **`tests/visual-regression/`** (TypeScript, `@playwright/test`) drives the browser and does the actual pixel-diffing — the only piece that needs the real snapshot-testing feature.
- **`src/Lanyard.VisualTestFixtures/`** (.NET console app, project-references `Lanyard.Infrastructure`) seeds fixture data using the real EF models as the schema source of truth, rather than duplicating table/column knowledge in hand-written SQL from the TypeScript side. `globalSetup.ts` shells out to it (`dotnet run --project ...`) once before the suite runs, and it writes the fixed fixture GUIDs to `.fixture-ids.generated.json` so the TS spec files can build URLs (`/manage/dashboards/{id}`, `/training/{assignmentId}`) without duplicating literal GUIDs in two languages.

Neither project is added to `LanyardApp.sln` / `LanyardApp.CI.slnf` — both only ever run inside the dedicated `visual-regression.yml` CI job (or the local Docker workflow below), never via `dotnet build LanyardApp.sln` or the Windows `build-and-test` job. Adding either back to the `.sln` would trip that job's sln/slnf drift-check for no benefit — neither project needs Windows IDE discoverability, since neither can run meaningfully without a live Postgres, a running server, and installed browsers.

**This suite is CI-only.** It is *not* part of CLAUDE.md's "before marking a task complete, run `dotnet test src/Lanyard.Tests/...`" gate, and the `verify` skill's fast in-memory-friendly loop doesn't touch it either — don't fold it into routine per-task verification.

## Running it locally

Baselines **must** be generated on the same OS/font-rendering environment as CI (`ubuntu-24.04` — see `playwright.config.ts`'s pinned `runs-on`), not on a contributor's local Mac/Windows machine; Playwright's screenshot comparison is sensitive to anti-aliasing differences across operating systems, and a baseline generated on the wrong OS will produce permanent false-positive diffs against CI. If you're not already on a matching Linux environment, generate baselines inside a container using the same Ubuntu base as CI, or accept CI's own regenerated screenshots as the source of truth for a PR (`--update-snapshots` run in the CI job manually, then commit the artifact) rather than trusting a local run.

Use your own **disposable** Postgres for local runs — not the shared `lanyard-postgres` dev container the `verify` skill drives (that has real dev data other sessions rely on; seeding fixture rows into it is safe in isolation but repeated runs and a mismatched schema state are not worth the risk):

```bash
docker run -d --name lanyard-visual-test-pg -p 5433:5432 \
  -e POSTGRES_DB=lanyarddb -e POSTGRES_USER=lanyard_dev -e POSTGRES_PASSWORD=lanyard_dev_password \
  postgres:17

export ConnectionStrings__DefaultConnection="Host=localhost;Port=5433;Database=lanyarddb;Username=lanyard_dev;Password=lanyard_dev_password"
dotnet ef database update --project src/Lanyard.Infrastructure --startup-project src/Lanyard.Server/LanyardApp

# Launch the server on a port that won't collide with anyone else's (see the verify skill's
# "port already in use" guidance before picking 5096 itself):
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5197 \
  dotnet run --project src/Lanyard.Server/LanyardApp/Lanyard.App.csproj --no-launch-profile --no-build
```

Then, in another shell:

```bash
cd tests/visual-regression
npm install                          # first time only
npx playwright install chromium      # first time only
export BASE_URL=http://localhost:5197
export VISUAL_TESTS_CONNECTION_STRING="Host=localhost;Port=5433;Database=lanyarddb;Username=lanyard_dev;Password=lanyard_dev_password"

npm test                             # run against committed baselines
npm run test:update-snapshots        # regenerate baselines after an intentional UI change
```

`globalSetup.ts` runs the fixture seeder automatically before either command — no separate seeding step needed. Tear down the disposable Postgres afterward: `docker rm -f lanyard-visual-test-pg`.

Whoever makes an intentional UI change regenerates the affected baseline PNGs and commits them alongside their code change, the same way you'd update a golden file — CI just enforces that nobody forgot to.

## What's covered, and the two coverage gaps this repo's architecture creates

Five screens × two viewports (phone 390×844, desktop 1440×900, per CLAUDE.md): the dashboards list, a populated dashboard (`/manage/dashboards/{id}`, `EditDashboard.razor` loading in its default read-only preview mode), the music page (first playlist auto-loaded), a training course step (`/training/{assignmentId}`), and the DMX desk.

**A fresh CI Postgres has nothing in it beyond `DatabaseSeeder`'s baseline** (roles + the dev admin user) — no dashboard, no songs, no course. `Lanyard.VisualTestFixtures/Program.cs` seeds exactly what these five screens need, with fixed GUIDs chosen not to collide with `ApplicationDbContext`'s own seed constants:

| Fixture | Feeds |
|---|---|
| One `Dashboard` + one `TextAreaWidget` | dashboards list, dashboard detail |
| One `Playlist` + two `Song`s + `PlaylistSongMember` rows | music page |
| One `Course` + one `CourseSection` + one `CourseAssignment` (owned by the seed admin) | training course step |
| One `Client` row | DMX desk's client-selector dropdown (see below - this does *not* make it "connected") |

**Widget choice matters for the dashboard fixture.** `TextAreaWidget` is deliberately the only widget seeded: it's the one widget type in `DashboardModels.cs` with genuinely static content. `DigitalClockWidget` ticks, `GreetingWidget` changes text by wall-clock hour and self-refreshes every 60s, and several others (`KioskHealthWidget`, `ProjectionStatusWidget`, etc.) pull live state — seeding any of those would need masking or would flake outright.

**The DMX desk cannot reach a "connected" state on Linux CI at all**, not as a scoping choice but as a hard platform constraint: `ClientSelector`'s populated/connected state is driven entirely by `SignalRControlHub.ConnectedIds`, an in-memory set that only a real SignalR-connected client populates — and the only real client is `Lanyard.Client.exe`, a Windows-only WPF binary (see the `kiosk-client-dev-stack` skill). There is no DB row or API call that fakes "connected." `dmx-desk.spec.ts` therefore screenshots the desk's default *no-client-selected* state ("Select a client to view scenes.") — still a real, useful regression check for layout/chrome, but it cannot catch a regression in the live fader/scene-tile rendering path. Extending coverage there would mean either running a real Windows kiosk-client job (a much bigger CI investment) or building a client-side SignalR test double that fakes a connection well enough to populate `ConnectedIds` - neither exists today.

## Stability strategy (why each wait exists)

Blazor Server's own two Playwright touchpoints (the `verify` skill, `.claude/scripts/record-video/record-video.mjs`) both use hard-coded `waitForTimeout` sleeps, because a plain `networkidle` wait can fire before the SignalR circuit finishes connecting — the page looks interactive (static prerendered HTML) but isn't yet, so clicking too early is a silent no-op. This suite avoids blind sleeps in favor of specific signals:

- **Login** (`login.ts`): waits for `networkidle` right after `page.goto("/login")`, *before* clicking the company-picker button — this was load-bearing in practice, not defensive: without it, the login flow flaked with a 30s timeout waiting for the username field, because the click landed before the circuit connected. Confirmed by direct testing during implementation.
- **Per-screen content waits**: each spec waits for a DOM signal that only appears once real data has loaded (a specific grid row, `.dashboard-widget-host`, `.song-list-grid`, `.take-course-section-body`, the "Scenes" card text) — not a generic "page loaded" check.
- **Toast dismissal**: `ClientSelector` fires a "Successfully loaded N connected clients" toast on every Music/DMX page load; `waitForToastToClear` waits for it to hide (or times out harmlessly if none appeared) so a screenshot doesn't land mid-fade.
- **`animations: "disabled"`** (set globally in `playwright.config.ts`'s `expect.toHaveScreenshot`) freezes CSS animations/transitions/finite `requestAnimationFrame` loops automatically — no hand-rolled CSS override needed for the common case.

## Diff tolerance

`playwright.config.ts` sets a tight global default (`maxDiffPixelRatio: 0.005`) rather than a loose one — a loose global tolerance risks silently absorbing small real regressions on static, content-heavy screens. One consequence observed directly while validating this suite: a fixed-size visual defect (e.g. a stray outline around a small widget) can clear the 0.5% threshold on the phone viewport's ~330K total pixels but fall *under* it on desktop's ~1.3M total pixels, purely because the same absolute pixel count is a smaller fraction of a bigger frame. If a real regression report looks suspiciously viewport-specific, check whether it's actually this ratio effect before assuming the desktop render is fine — a per-call tighter override or a scoped-locator screenshot (rather than the whole page) narrows the denominator and restores sensitivity.

**Generate baselines on a real GitHub Actions runner if in doubt, not just "a matching-looking Linux box."** During implementation, baselines generated in a local sandbox (also Ubuntu, also amd64) passed 9/10 tests unchanged on the actual `ubuntu-24.04` GitHub-hosted runner, but the training-course-step phone baseline failed with a ~1% diff — the sandbox and the real runner wrapped the exact same sentence across a different number of lines (a subtle font-metrics difference tipping a line-break threshold), not a rendering bug on either side. Long text near a column-width boundary is the most sensitive case for this. If a freshly generated baseline fails on its first real CI run with a small, localized diff around text, don't assume the suite is broken — download that run's `visual-regression-test-results` artifact (`gh run download <run-id> -n visual-regression-test-results`) and look at the `*-diff.png`; if it's a benign reflow like this, adopt the CI run's own `*-actual.png` as the new baseline (`cp` it over the committed snapshot) rather than re-guessing locally.

## CI job shape (`.github/workflows/visual-regression.yml`)

- Triggers only on `pull_request`, path-filtered to `**/*.razor`, `**/*.razor.css`, `wwwroot/**`, `Components/**`, plus `Lanyard.Infrastructure/**` and `LanyardServices/**` (a model or service-layer change can alter a screen's rendered output without touching a `.razor` file) and the suite/fixture-project paths themselves.
- `runs-on: ubuntu-24.04` (pinned, not `ubuntu-latest` — that alias rolls forward and a GitHub-side image bump can shift font rendering enough to invalidate every baseline with no code change on our side; bump this deliberately, alongside a full baseline regeneration, not implicitly).
- A `postgres:17` service container with an explicit health check (`pg_isready`) — this is the first Postgres service container anywhere in this repo's CI, so there's no existing pattern here to diverge from if you're extending it.
- Checks out the PR head commit explicitly and re-attaches to a real local branch (`git checkout -B "$GITHUB_HEAD_REF"`) right after — `actions/checkout` always leaves the workspace in detached HEAD, but `publish-screenshots.sh` derives its storage path from `git rev-parse --abbrev-ref HEAD` and refuses to run detached (by design — that's what makes storage branch-name-keyed rather than PR-number-keyed, so it works before a PR even exists). Also build config must stay in sync across build/migrate/launch steps: `dotnet ef database update ... -c Release --no-build` looked for the startup project's build output in `bin/Debug` regardless of the `-c Release` flag on one CI run, so the job builds and runs everything in the default Debug config throughout rather than mixing configurations.
- Builds `Lanyard.App.csproj` and `Lanyard.VisualTestFixtures.csproj`, migrates, launches the built server DLL directly in the background (not `dotnet run`, to skip a redundant MSBuild evaluation), polls the port, then runs `npm ci` + Playwright browser install + `npm test` inside `tests/visual-regression/`.
- On failure: `build-diff-manifest.mjs` walks Playwright's `test-results/**/*-{expected,actual,diff}.png` output (a layout `publish-screenshots.sh` knows nothing about) into the `{file, viewport, caption}` manifest shape that script expects, then invokes it with `--pr` to post the failing images to the PR. `actions/upload-artifact` also grabs the full `test-results/` tree as a fallback.
- **Not (yet) wired into branch protection as a required check.** It fails red on a real diff, satisfying "fails the build," but promoting it to a required status check in the `dev`/`main` rulesets is a manual GitHub settings decision, deliberately left for a human once the suite has proven non-flaky in practice — Blazor Server's circuit-timing quirks (see above) are exactly the kind of thing that can make a new CI check noisy before it's earned trust.

## Adding a new screen

1. Add whatever fixture rows the screen needs to `src/Lanyard.VisualTestFixtures/Program.cs`, with new fixed GUIDs that don't collide with the existing ones there or with `ApplicationDbContext`'s seed constants. If the screen needs the logged-in user's own id (ownership-checked routes, like `CourseAssignment`), use `ApplicationDbContext.SeedAdminUserId`.
2. If the new fixture ids need to appear in a URL, add them to the JSON object `Program.cs` writes out, and read them via `getFixtureIds()` in the spec (don't hardcode the GUID a second time in TypeScript).
3. Write the spec under `tests/visual-regression/tests/`: log in, navigate, wait for a real positive-content signal (not a fixed sleep), call `expect(page).toHaveScreenshot("name.png")`.
4. Audit the screen for anything live/non-deterministic (clocks, relative timestamps, load-time toasts, animated indicators) and either avoid seeding that content or pass `mask: [locator]` to the `toHaveScreenshot` call.
5. Generate baselines locally per "Running it locally" above (both viewports come from `playwright.config.ts`'s two projects automatically), review them, and commit the resulting `tests/visual-regression/tests/<spec>.spec.ts-snapshots/*.png` files alongside the code change.

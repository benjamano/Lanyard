---
name: kiosk-client-dev-stack
description: Launches the full LanyardApp dev stack — server .exe plus the real kiosk client .exe — together, for end-to-end SignalR/DMX/projection verification. This is heavier than the `verify` skill, which only launches the server via `dotnet run --launch-profile http` and drives the Blazor UI through Playwright without any kiosk client involved. Use this skill specifically when the task needs the actual kiosk client connected (SignalR negotiate, DMX scene playback, client-side projection rendering, testing the client/server handshake) — not for ordinary UI-only verification.
---

# Launching the kiosk-client dev stack end-to-end

Use the `verify` skill instead if the task is purely about the Blazor Server UI and doesn't need a connected kiosk client — it's a lighter loop. Reach for this skill when the kiosk client itself needs to be running and connected.

## Build note

A full solution build can be blocked by file locks while the app is running under the debugger. When that happens, individual projects still build standalone: `src/Lanyard.Client/Lanyard.Client.csproj` and `src/Lanyard.Server/LanyardServices/Lanyard.Services.csproj` build fine even while `LanyardApp` is locked, and Razor compile errors still surface during a locked `LanyardApp` build (only the final DLL-copy step fails) — so `.razor` changes can be validated without stopping the running app.

## Launching the server

Run `Lanyard.App.exe` with:
- **Working directory** set to the `LanyardApp` project folder, so `appsettings.local.json` (which holds `Clients:SharedSecret`) loads.
- `ASPNETCORE_URLS` including **https on 7175** — the kiosk client's config points at `https://localhost:7175`, so an http-only launch leaves the client unable to connect at all.
- `ASPNETCORE_ENVIRONMENT=Development` — without it, `appsettings.Development.json` (which holds the local docker-compose Postgres `ConnectionStrings:DefaultConnection`) never loads, and the app throws `InvalidOperationException: Connection string 'DefaultConnection' is not configured` on startup, since `appsettings.json` deliberately ships an empty connection string.

## Launching the client

The real, runnable binary is `build\Lanyard.Client.exe` at the **repo root** (the csproj sets `OutputPath=..\..\build\`). Don't launch from `src\Lanyard.Client\bin\Debug\net10.0-windows\` — that can hold stale leftovers that predate the current server handshake and will 401 against a fresh server. (If the client fails to launch entirely with a runtime-not-found-looking error, see the `client-build-troubleshooting` skill — that's usually a stray-DLL issue in `build\`, not this handshake issue.)

Required client env vars:
- `LANYARD_SERVER_URL`
- `LANYARD_CLIENT_SHARED_SECRET` — both of these are sourced from `%APPDATA%\LanyardClient\config.json` in a real install.
- `LANYARD_CLIENT_ID` — the specific client's row id in the `Clients` table.
- For test runs, also set `LANYARD_CLIENT_SKIP_ADDING_STARTUP_TASK=true` and `LANYARD_CLIENT_SKIP_ADDING_WATCHDOG_STARTUP_TASK=true` to avoid registering Windows scheduled tasks as a side effect of testing.

## Troubleshooting the handshake

If the server 401s a SignalR negotiate call, check the `secret` query param the client sends against the shared-secret middleware in `Program.cs` — it's registered before `MapHub("/websocket")` and rejects the negotiate call before SignalR ever sees it if the secret is missing or wrong.

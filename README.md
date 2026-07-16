# Lanyard CRM & Staff Managment
This repository is for Lanyard, a Customer Relationship and Staff Managment System.
For any questions about this system, contact [Ben Mercer](mailto:benmercer76@btinternet.com) at [benmercer76@btinternet.com](mailto:benmercer76@btinternet.com)

## About
Lanyard is the [Zone Laser Scoreboard's](https://github.com/benjamano/Zone-Laser-Scoreboard) replacement.
Written in C# Blazor, it will allow for quicker development as well as a higher quality UI experience.

It uses [Blazor FluentUI V4](https://www.fluentui-blazor.net/) as the main Component library.

This project is currently maintained by the two contributers [Ben Mercer](https://github.com/benjamano) and [Cadan Arnold](https://github.com/TheTinyGiant240)

## Project Structure

### Lanyard Server

Lanyard Server is in charge of the Staff and Managment side of Lanyard.
It handles internal messaging such as Staff Announcments, Tracking centre statistics, and co-ordinates all Lanyard Clients connected to it. 

### Lanyard Reach

Lanyard Reach will be responsible for hosting the Customer facing site, it will handle bookings and any other customer interactions as well as giving the customers the ability to learn more about your company.

### Lanyard Client

Lanyard Client is an internal system designed to be used as a single control point for many aspects:

- Laser Tag Game Managment and Control
- Point Of Sales (POS) Support (Coming Soon)
- Kitchen Managment including QR Code and online food odering Support (Coming Soon)
- Dynamic Dashboards for Static Kiosks which can include all kinds of information
- Central Music Centre Music Control.

One-Time setup per Client, all customisable via [Lanyard Server](#LanyardServer).

## Local Development Setup

### Database

Local development uses a throwaway PostgreSQL running in Docker — no remote or shared
database credentials are needed on a developer machine.

1. Start the database (and optional Adminer UI at http://localhost:8081):

   ```bash
   docker compose up -d
   ```

2. Apply the latest migrations:

   ```bash
   dotnet ef database update \
     --project src/Lanyard.Infrastructure \
     --startup-project src/Lanyard.Server/LanyardApp
   ```

3. Run the server (`dotnet run` in `src/Lanyard.Server/LanyardApp`, or the **Debug LanyardApp**
   launch config in VS Code, which starts the database container automatically).

The development connection string lives in `appsettings.Development.json` and points at the
container defined in `docker-compose.yml`. Stop the database with `docker compose down`
(add `-v` to also wipe the data volume).

### Production configuration

The production database connection string is supplied at runtime via the
`ConnectionStrings__DefaultConnection` environment variable — it is **never** committed to
`appsettings.json`. The app will refuse to start if it is not set.

## Running the Lanyard Client

The Lanyard Client is a Windows desktop application that runs on kiosk machines, handling local music playback, projection programs, and laser game packet sniffing.

### Installation

1. Go to the [Releases](../../releases) tab on GitHub.
2. Download the latest release installer for Windows.
3. Run the installer named `LanyardClient-win-Setup.exe`, it will then install the client and enable automatic updates.

### Configuration

During the first launch, you will be prompted to set the Environment Variables for the Server URL.
If you are running Lanyard Server on the same PC, the URL should be `https://localhost:7175`.

The first launch will also as if you would like to set the `LANYARD_CLIENT_SKIP_ADDING_WATCHDOG_STARTUP_TASK` Envrionment Variable.
This variable, if set to `false` will automatically enable the "Watchdog" system, which will automatically start at logon, and will automatically start Lanyard Client, incase of power loss or any PC restarts.

### Auto-Updates

The client uses Velopack for automatic updates. On startup (outside of Development mode), it will check for a new release and apply it automatically. No manual reinstallation is needed when a new version is published.

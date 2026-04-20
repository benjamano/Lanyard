# Lanyard CRM & Staff Managment
This repository is for Lanyard, a Customer Relationship and Staff Managment System.
For any questions about this system, contact [Ben Mercer](mailto:benmercer76@btinternet.com) at [benmercer76@btinternet.com](mailto:benmercer76@btinternet.com)

## About
Lanyard is the [Zone Laser Scoreboard's](https://github.com/benjamano/Zone-Laser-Scoreboard) replacement.
Written in C# Blazor, it will allow for quicker development as well as a higher quality UI experience.

This project is currently maintained by the two contributers [Ben Mercer](https://github.com/benjamano) and [Cadan Arnold](https://github.com/TheTinyGiant240)

## Running the Lanyard Client

The Lanyard Client is a Windows desktop application that runs on kiosk machines, handling local music playback, projection programs, and laser game packet sniffing.

### Installation

1. Go to the [Releases](../../releases) tab on GitHub.
2. Download the latest release installer for Windows.
3. Run the installer, it will then install the client and set up automatic updates.

### Configuration

During the first launch, you will be prompted to set the Environment Variables for the Server URL.
If you are running Lanyard Server on the same PC, the URL should be `https://localhost:7175`.

### Auto-Updates

The client uses Velopack for automatic updates. On startup (outside of Development mode), it will check for a new release and apply it automatically. No manual reinstallation is needed when a new version is published.

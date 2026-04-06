# HPPAQDeploy

A network deployment tool for HP driver, BIOS, and firmware updates powered by HP Image Assistant (HPIA).

## Overview

HPPAQDeploy scans your network for HP devices, analyzes them for missing drivers, BIOS, and firmware updates using HP Image Assistant, and deploys selected updates remotely — all from a single desktop application.

## Features

- **Network Scanning** — Ping sweep with CIDR range support, WMI-based HP device discovery
- **Device Groups** — Organize devices by model, department, or location for batch operations
- **HPIA Analysis** — Remotely scan devices for missing drivers, BIOS, firmware, and software updates
- **Selective Deployment** — Choose specific updates per device or install all at once
- **Remote Reboot** — Send reboot commands with 60-second user warning
- **Deployment History** — Track all installations with success/failure status
- **Email Notifications** — Alerts for scan completion, critical updates, and deployment results
- **Scheduled Scans** — Automatic recurring network discovery
- **Credential Management** — DPAPI-encrypted credential storage
- **BIOS Password Support** — Automatic BIOS password handling during updates
- **Export Reports** — CSV and HTML compliance reports

## Requirements

- Windows 10/11 or Windows Server 2016+
- .NET 8.0 Runtime (included in self-contained build)
- Domain admin credentials for remote device access
- HP Image Assistant 5.3.4 (`hp-hpia-5.3.4.exe`) placed alongside the application
- WMI/DCOM and SMB (port 445) access to target devices

## Quick Start

1. **Download** `HPPAQDeploy.exe` and `hp-hpia-5.3.4.exe` from [Releases](https://github.com/CarfoCx/HPPAQDeploy/releases)
2. Place both files in the same folder
3. Run `HPPAQDeploy.exe`
4. Go to **Credentials** tab and add domain admin credentials
5. Go to **Devices** tab, click **Add Hosts**, enter your network range, and click **Scan**
6. Go to **Groups** tab, create a group, and add devices by model or individually
7. Go to **Deploy Updates** tab, select your group, and click **Scan Group**
8. Select updates and click **Install Selected** or **Install All Updates**

## How It Works

HPPAQDeploy uses:
- **WMI/DCOM** (`Win32_Process.Create`) for remote command execution — no WinRM required
- **SMB admin shares** (`\\host\C$`) for file transfer
- **Scheduled Tasks** for running HPIA in the user's session
- **HP Image Assistant** for update analysis and installation

### Deployment Flow

```
Scan Network → Discover HP Devices → Create Groups → Scan for Updates → Deploy Selected Updates → Reboot if Needed
```

## Architecture

| Project | Description |
|---------|-------------|
| `HPPAQDeploy.App` | WPF GUI (MVVM with CommunityToolkit.Mvvm, MaterialDesign theme) |
| `HPPAQDeploy.Core` | Models and interfaces |
| `HPPAQDeploy.Infrastructure` | WMI/DCOM, SMB, HPIA orchestration, SQLite via EF Core |
| `HPPAQDeploy.Shared` | Configuration, logging, helpers |

## Configuration

Settings are stored in `Data/settings.json` and can be modified from the **Settings** tab:

- **Performance** — Ping/WMI/Deploy concurrency, timeouts
- **Scheduled Scans** — Enable automatic network discovery
- **Email Notifications** — SMTP configuration for alerts
- **BIOS Passwords** — For BIOS-protected devices
- **Update Catalog** — Sync HP's update catalog for offline scanning

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+1–8 | Navigate tabs |
| F1 | Show keyboard shortcuts |
| F5 | Refresh current view |

## Building from Source

```bash
# Requires .NET 8.0 SDK
dotnet build src/HPPAQDeploy.App/HPPAQDeploy.App.csproj

# Publish self-contained single-file exe
dotnet publish src/HPPAQDeploy.App/HPPAQDeploy.App.csproj -c Release -o publish
```

## License

This project is provided as-is for internal IT administration use.

## Contributing

Issues and pull requests welcome at [github.com/CarfoCx/HPPAQDeploy](https://github.com/CarfoCx/HPPAQDeploy).

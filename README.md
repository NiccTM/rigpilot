# PC Helper

PC Helper is an open-source Windows control centre for desktop PCs. It provides a single capability-gated dashboard for hardware inventory, monitoring, profiles, cooling, automation, lighting bridges, and diagnostics.

The current implementation is an early `0.2` foundation. It is deliberately read-only for unqualified low-level hardware controls. A control is enabled only after the adapter can discover its valid range, read the current value, apply a candidate, verify it, and restore the previous or firmware-default value.

## Current capabilities

- Windows 10 22H2 and Windows 11 x64 inventory through CIM.
- LibreHardwareMonitor sensor discovery with graceful degradation when privileged hardware access is unavailable.
- Detection of known competing monitoring, fan, GPU, AIO, and RGB applications.
- Versioned local named-pipe protocol between the dashboard, CLI, and service.
- Transactional typed profile engine with validation, journalling, verification, and rollback.
- Fan-curve interpolation, hysteresis, slew limiting, mixed sensors, and stale-source safety decisions.
- SQLite state and bounded sensor-history storage under `%ProgramData%\PCHelper`.
- Structured JSON service logs retained for seven days with a 50 MB cap.
- WPF dashboard and tray controls.
- Redacted compatibility report generation and opt-in report API.
- Headless `pchelper-cli probe --json` diagnostics.

Low-level CPU/GPU tuning, fan writes, and OpenRGB writes remain capability-gated until hardware-in-loop qualification is complete. PC Helper never bundles WinRing0 and never asks users to disable Windows security features.

## Build

Requirements:

- Windows x64.
- .NET SDK 10.0.301 or a compatible newer 10.0 feature band.
- Node.js for the optional report API.

```powershell
$dotnet = "$HOME\.dotnet\dotnet.exe"
& $dotnet restore PCHelper.sln
& $dotnet build PCHelper.sln -c Release --no-restore
& $dotnet test PCHelper.sln -c Release --no-build
```

Run a read-only local probe:

```powershell
& $dotnet run --project src/PCHelper.Cli -- probe --json
```

Run the service interactively for development, then start the dashboard:

```powershell
& $dotnet run --project src/PCHelper.Service
& $dotnet run --project src/PCHelper.App
```

## Safety and support labels

Every capability has one of these states: `Verified`, `Experimental`, `ReadOnly`, `Blocked`, `Unsupported`, or `Faulted`. Unsupported and unverified write controls are not silently exposed.

Windows 10 compatibility is best effort because Microsoft ended standard support on 14 October 2025. Windows 11 24H2 and later is the primary target.

## Licence

PC Helper is licensed under the GNU General Public License version 3 only. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

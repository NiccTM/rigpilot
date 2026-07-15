<p align="center">
  <img src="assets/brand/rigpilot-mark.svg" width="96" alt="RigPilot mark" />
</p>

# RigPilot

RigPilot is an open-source Windows control centre for desktop PCs. It provides a single capability-gated dashboard for hardware inventory, monitoring, profiles, cooling, automation, lighting bridges, and diagnostics.

> **Brand continuity:** RigPilot is the public name. Existing `PCHelper` service, pipe, CLI, and `%ProgramData%\PCHelper` identifiers remain unchanged so existing installations, state, and permissions upgrade safely.

The current workspace is a `0.4.0-alpha` development build. It adds commissioning, health/recovery, monitoring, gaming, lighting-layout, user-agent, and vendor-telemetry foundations, but is not a hardware-qualified release. Unqualified low-level controls are never presented as certified. A bounded adapter may expose an `Experimental` path only when it can prepare, apply, read back, roll back, and return to firmware/default control; the user must acknowledge both global and exact-device risk before an operation can start.

## Current capabilities

- Windows 10 22H2 and Windows 11 x64 inventory through CIM.
- Versioned read-only compatibility classification for Ryzen/Threadripper 1000-9000, Intel Core 6th-14th/Core Ultra, NVIDIA GTX 700-16 and RTX 20-50/professional, Radeon RX 400-9000/professional, Arc A/B/professional, ASUS/MSI/Gigabyte/ASRock/Biostar/EVGA/Supermicro/Colorful/Maxsun/OEM boards, HID LampArray, and expanded common RGB/peripheral families. A family match never enables a write path.
- PCI-subsystem GPU-board tagging for ASUS/ROG/TUF, Gigabyte/AORUS, MSI/Mystic Light, ZOTAC/SPECTRA, EVGA/K|NGP|N, ASRock/Polychrome, Sapphire, PowerColor, XFX, PNY, Palit, Gainward, Yeston, and common OEM identities; further board-line labels require an explicit name. The tag feeds RGB routing and qualification guidance only; it never enables a native GPU lighting protocol. See `docs/gpu-board-rgb-registry.md`.
- LibreHardwareMonitor sensor discovery with graceful degradation when privileged hardware access is unavailable.
- Restartable Adapter Host for LibreHardwareMonitor native access; a per-launch pipe/token authenticates every command, and a kill-on-close Windows job object contains child failures.
- Detection of known competing monitoring, fan, GPU, AIO, and RGB applications.
- Versioned local named-pipe protocol between the dashboard, CLI, and service, with a feature/version handshake that locks service writes if the installed app and service are from different release lines.
- Transactional typed profile engine with validation, journalling, verification, and rollback.
- Protocol-v2 capabilities and profiles with hazard classes, bounds sources, reset/read-back guarantees, ownership, boot policy, manual-voltage rules, and lossless v1 migration. The dashboard, automation, and game selectors apply generated V2 Windows-power profiles through typed transactions.
- Advanced cooling graphs with linear/graph/trigger/flat/sync/mix/feedback nodes, RPM calibration, avoid bands, separate rise/fall slew, hysteresis, time averaging, and a service-side one-second evaluation loop. The loop holds a last-good source for two missed polls, then commands maximum duty and requests firmware/default recovery on the third.
- Experimental fan-control API path through LibreHardwareMonitor, blocked automatically when Fan Control, MSI Afterburner, or another overlapping owner is running.
- Fan calibration from 100% downward in 5 percentage-point steps, explicit case-fan stop opt-in, cancellation, rollback, and persisted boot recovery. A long calibration requires a visually observed physical header, not a software alias.
- Stable median RPM sampling records the per-output low-speed plateau, effective floor, and first responsive duty. A stable non-stopping output can use a measured nonzero curve; zero-RPM requires repeated stop/restart verification. A flat or mismatched tachometer remains blocked.
- After a physically observed commissioning report is saved, the dashboard can reuse its persisted exact-session calibration after restart to generate a conservative per-output CPU/GPU temperature graph and an inactive V2 profile. The graph's positive requests are clamped to that output's measured floor; creating it does not apply a fan command.
- Advanced Lab adds a calibration-bound manual curve studio: two to eight explicit temperature:duty points, independent rise/fall hysteresis and response time, fixed measured nonzero floor, and required full-speed final point. It saves an inactive typed graph/profile and never applies a fan command during editing or saving. See [competitor-parity.md](docs/competitor-parity.md) for the clean-room parity ledger.
- Guided per-header commissioning: paired same-device RPM sensor, explicit 60% bounded two-second identification pulse, visual-observer preflight, qualification report, and recovery-to-default. A persisted physical-output role registry prevents generic Super I/O labels from bypassing CPU-fan/pump safeguards; CPU fans cannot use zero-RPM, and a stored pump remains read-only until exact pump-specific qualification exists.
- Persisted cooling qualification reports that record header mapping, repeated restart evidence, emergency-response timing, suspend/resume recovery, and default-reset state without treating an incomplete step as a pass.
- Typed local health rules for temperature, stale sensors, pump/fan RPM, WHEA events, and display-driver resets; active alerts appear in the dashboard/tray and retain a bounded local event history. A requested emergency profile requires operator confirmation; safe mode disables client automation without silently issuing a hardware write.
- A one-click recommended baseline creates only conservative, notify-only CPU/GPU/pump and Windows-event rules. It never applies a profile, changes a control, or enters safe mode automatically.
- Startup recovery/safe-mode console for pending hardware state, automation suppression, local diagnostic export, and an explicit explanation of blocked capabilities.
- Monitoring workspace with rolling per-sensor trends, aliases, pinned metrics, a normalized four-series comparison overlay with native-unit legends, a profile/conflict/safety timeline, and local CSV export.
- Local, privacy-minimised hardware-evidence export containing identity, capability, reset, trace, and qualification state. It is bounded and does not upload data.
- Single-domain bounded auto-tuning with voltage exclusion, thermal/power/event screening, a 10-minute final screen, provisional generated profiles, and a pending-boot sentinel.
- SQLite-backed process, foreground-app, schedule, session-lock, idle, and registered-hotkey automation rules with entry/exit debounce, priority, manual override, and switch cooldown.
- Per-device RGB compatibility routing: Windows Dynamic Lighting first, an explicitly enabled loopback-only OpenRGB SDK bridge second, qualified built-in adapters third, then read-only direct qualification. It blocks overlapping lighting writers and never infers a raw USB/HID write from a manufacturer name. See `docs/rgb-compatibility.md`.
- Optional loopback-only OpenRGB SDK bridge with protocol negotiation, exact controller enumeration, static colour, brightness scaling, and off state. OpenRGB is not bundled.
- Windows Dynamic Lighting/LampArray discovery, per-device enable/disable, brightness, physical LED zones, and per-index static colour.
- Declarative solid, gradient, wave, breathing, spectrum, temperature, notification/game-event, audio-spectrum, screen-ambience, and blend effects.
- `PCHelper.EffectHost` for trusted JavaScript lighting effects: entry-point SHA-256 revalidation, WebView2 host-object/network/navigation/download/permission denial, a 512 MB process-job limit, bounded LED/frame inputs, and a watchdog.
- Clean-room MSI Afterburner CFG and Fan Control JSON import previews with bounds, unsupported mappings, manual-voltage labels, curves, calibration, and avoid-band conversion. Saving persists a typed profile/graph only; it never applies hardware.
- Signed `.pcha` adapter-pack inspection/install foundation with Ed25519 signatures, payload hashes, protocol and permission checks, exact developer-hash trust, and archive containment.
- A separate per-user named-pipe runtime for workflows, effects, games, macros, trusted scripts, OSD layouts, capture presets, explicit still-image snapshots, and local overlay presentation.
- Same-session monitor-brightness discovery on the Devices page. Every Windows logical display remains visible: external DDC/CI monitors and Windows-managed WMI panels can expose an `Experimental` bounded write path; inaccessible or unsupported displays retain a precise read-only reason. A brightness write requires direct selected-display confirmation, a unique request key, and immediate read-back, with best-effort restoration on mismatch. It is not callable from profiles or automation.
- `PCHelper.AutomationHost` for individually trusted hash-bound scripts; it refuses LocalSystem, enforces timeouts, captures bounded output, and terminates child process trees.
- Same-user macro playback through validated `SendInput` steps with single-playback exclusion and guaranteed release of held keys/buttons after failure or cancellation.
- Visible same-user macro recording, typed key-press editing, explicit test action, cancellation that discards raw input, and per-game macro assignment.
- Validated OSD frame formatting plus a local transparent, non-activating desktop overlay with no injection or RTSS dependency. Per-user OSD presentation settings include a global hotkey, display target, screen anchor, opacity, and scale. Explicit same-user PNG snapshots require visible-session confirmation and are written only below `Pictures\\RigPilot\\Snapshots`; they use bounded GDI/PrintWindow capture, not a service process. A journalled capture-session coordinator covers start/stop, encoder failure, target removal, abort, and final metrics. RTSS discovery is read-only; Windows Graphics Capture capability is preflighted without video or audio capture writes.
- Optional `RigPilot.GameBarWidget` UWP/MSIX companion with an exact-package-SID, read-only named-pipe bridge to the user agent. It cannot access the service, hardware, profiles, scripts, macros, or capture actions.
- NVML read-only RTX telemetry and public bound discovery for fan targets, power limits, and any driver-exposed voltage-frequency offsets. Exact-device Ryzen 5800X/RTX 3090 and AMD/Intel/NVIDIA qualification plans expose the missing apply/read-back/reset/driver gates before a write adapter can exist. Aura and Lian Li/direct-USB qualification plans remain read-only in Adapter Host until containment and physical tests pass.
- Transactional exact-identity takeover and update coordinators with startup/lease rollback, firmware-default verification, rollback-package export, reboot sentinels, post-boot verification, and recovery-required states. Windows takeover requires valid service Authenticode, stored exact consent, and verified defaults; current unsigned builds are blocked before mutation. The service-owned driver executor accepts only a staged, exact INF package with a trusted catalog, publisher, canonical package hash, OEM rollback export, explicit confirmation, and a signed service image before calling `PnPUtil`; it rejects firmware and BIOS payloads. Release scripts sign and verify every RigPilot binary, MSI, and bundle when supplied a valid Code Signing certificate.
- Bounded local Steam, Epic, GOG, Xbox, and standalone game-manifest scanning without downloaded metadata.
- SQLite state and bounded sensor-history storage under `%ProgramData%\PCHelper`.
- Structured JSON service logs retained for seven days with a 50 MB cap.
- Responsive, fully dark WPF dashboard with nine task-focused pages, Simple/Advanced Lab modes, searchable inventory, non-modal status feedback, keyboard navigation, and tray controls.
- Redacted compatibility report generation and opt-in report API.
- Headless `pchelper-cli probe --json`, `pchelper-cli trace --json`, `pchelper-cli operation [--id OPERATION_ID] --json`, and `pchelper-cli runtime-preflight --json` diagnostics, including `--local` to bypass an installed service during development. `scripts\Export-CaseFanCalibrationEvidence.ps1` normalizes one exact completed calibration as local read-only evidence and refuses to promote a no-stall result to restart-qualified.

No qualified Ryzen 5800X or RTX 3090 clock/power endpoint is exposed yet. RTX 3090 and NCT6798D fan writes have controlled reference-system evidence, but remain `Experimental`. RTX 3090 restart verification was attempted and correctly rejected after inconsistent low-duty hysteresis and an 85.2 °C hotspot safety abort; zero-RPM remains disabled and the conservative unverified floor is 50%. Header identity, live emergency timing, suspend, reboot, uninstall, signed Game Bar installation, and multi-system evidence are incomplete. Active competing writers block every overlapping control. Identity-bound takeover, JavaScript effects, macro playback/recording, local desktop OSD, explicit PNG snapshots, OSD formatting, capture coordination, typed health/recovery, and update transactions have tested software paths. A health rule can enter safe mode, while an emergency-profile request waits for operator confirmation rather than silently writing hardware. Production competitor/startup mutation, RTSS output, WGC video capture, Media Foundation/WASAPI audio capture, vendor update installation, and any direct USB device protocol remain disabled until signed platform executors and physical tests exist. OpenRGB writes are delegated to an explicitly enabled local server. RigPilot never bundles WinRing0 and never asks users to disable Windows security features.

**Current motherboard-fan gate:** the `NCT6798D` Fan #1 LocalSystem Adapter Host no-write `Prepare` path now passes on this reference PC after the normal named-pipe client changed from `Identification` to `Impersonation`. A disposable LocalSystem `direct-prepare` diagnostic and a temporary alpha-service Adapter Host preflight both reported `Prepared=true` with `Apply`, `Verify`, `Rollback`, and `Reset` structurally false. A fresh matching unsigned `0.4.0-alpha` service is currently staged beneath `%ProgramData%\RigPilot\LocalAlpha` through normal UAC; the previous `0.3` service registry key, data snapshot, and a paired restore script are retained. This is a reversible development deployment, not a signed installer upgrade. One authorized Fan #1 identification pulse completed at 60% for two seconds: the live adapter trace recorded successful `Prepare`, `Apply`, `Verify`, and `ResetToDefault`, and the paired RPM sensor read about 1479 RPM after reset. A later bounded 100%-to-0% calibration completed with stable measurements, a 1959 RPM maximum, and successful rollback. The tach remained near 546 RPM even at the API's 0% command, so the controller/fan did not demonstrate a stop state; no restart duty or repeated restart verification exists. The `CASE_FAN_1` name is a persisted software alias, not physical-header proof. Commissioning remains incomplete and no curve is enabled. Fan #5 is persistently classified as `Pump` / `AIO_PUMP` with its matched RPM sensor, so service policy blocks every pump pulse, preflight, calibration, graph, profile, and tuning path pending exact pump qualification. Saving that policy sent no fan command.

The current source further separates a user-declared alias from a visually observed physical header, binds each calibration to its exact commissioning session, requires physical observation before a full calibration, and activates a graph only after a stable measured nonzero floor. A saved report can produce an inactive, exact-output CPU/GPU mixed profile with no zero-RPM point even after an app/service restart; application still requires the Experimental and exact-device acknowledgements. A zero-RPM graph point remains blocked until repeated restart verification passes. These source safeguards are tested but are not yet deployed into the temporary unsigned alpha service; restore the installed service before any further live work with `scripts\Restore-LocalAlphaRuntime.ps1` through normal UAC.

The current calibration has also been exported through the read-only evidence script. Its normalized outcome is `CompletedNoStallObservedAtMinimumCommand`: this describes the controller/fan behaviour at the API floor, not a failed rollback or a stop/restart pass. It predates the current adaptive schema and lacks a physical-header observation, so it is not promoted to a curve; repeat the full-range characterization only after the physical header is observed.

The current one-system evidence is in `docs/qualification/reference-system.json`. Run `pchelper-cli qualification --ledger docs/qualification/reference-system.json` to evaluate the 18-system/two-independent-report release gate; it deliberately rejects this unsigned, incomplete reference record.

A current local read-only NVML probe finds NVML 610.62, two RTX 3090 fan targets, and a 100-385 W power range. It exposes no setter, does not change the `ReadOnly` state, and does not qualify a clock, power, or voltage-frequency write path.

Clarification: the source now contains the exact-identity Windows takeover executor, but its service-image Authenticode gate rejects every unsigned build before any process or startup mutation. Signed physical execution remains unqualified.

See [docs/feature-status.md](docs/feature-status.md) for the exact implemented/partial matrix.
See [docs/full-suite-plan-audit.md](docs/full-suite-plan-audit.md) for an item-by-item audit of the supplied full-suite plan and its remaining physical-release blockers.

## Build

Requirements:

- Windows x64.
- .NET SDK 10.0.301 or a compatible newer 10.0 feature band.
- Node.js for the optional report API.

```powershell
$dotnet = (Get-Command dotnet).Source
& $dotnet restore PCHelper.sln --locked-mode
& $dotnet build PCHelper.sln -c Release --no-restore
& $dotnet test PCHelper.sln -c Release --no-build
```

Build the optional Game Bar companion after installing the Visual Studio UWP/MSIX workload:

```powershell
$msbuild = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild .\src\PCHelper.GameBarWidget\PCHelper.GameBarWidget.csproj /restore /p:Configuration=Debug /p:Platform=x64
```

Create a production Game Bar candidate only with a valid code-signing certificate. This produces and verifies a signed MSIX but does not install it:

```powershell
.\scripts\build-gamebar.ps1 -Version 0.4.0-alpha -SigningCertificateThumbprint <certificate-thumbprint>
```

Run a read-only local probe:

```powershell
& $dotnet run --project src/PCHelper.Cli -- probe --local --json
```

Check release gates without producing, installing, or modifying an update package:

```powershell
.\scripts\Test-ReleaseReadiness.ps1 -QualificationLedger .\docs\qualification\reference-system.json
```

Verify that a published app, service, hosts, and CLI are one release line before WiX packages them:

```powershell
.\scripts\Test-RuntimePayload.ps1 -PayloadRoot .\artifacts\publish -ExpectedProductVersion 0.4.0
& $dotnet run --project src/PCHelper.Cli -- runtime-preflight --json
```

Run the service interactively for development, then start the dashboard:

```powershell
& $dotnet run --project src/PCHelper.Service
& $dotnet run --project src/PCHelper.App
```

Render every dashboard page with live read-only data for visual review:

```powershell
.\scripts\render-ui.ps1
.\scripts\render-ui.ps1 -OutputDirectory .\artifacts\ui-snapshots-compact -Width 960 -Height 640
```

The renderer uses the production XAML and view model but suppresses the tray, single-instance mutex, and global hotkey. It does not issue hardware writes. Generated PNGs remain under ignored `artifacts` directories.

## Safety and support labels

Every capability has one of these states: `Verified`, `Experimental`, `ReadOnly`, `Blocked`, `Unsupported`, or `Faulted`. Unsupported and unverified write controls are not silently exposed.

Windows 10 compatibility is best effort because Microsoft ended standard support on 14 October 2025. Windows 11 24H2 and later is the primary target.

## Licence

RigPilot is licensed under the GNU General Public License version 3 only. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

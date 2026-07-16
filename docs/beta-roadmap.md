# RigPilot competitive roadmap: beta to category leader

Status: proposed plan, 2026-07-15 (rev 2 — expanded to full competitive scope).
Goal: make RigPilot the single desktop control suite that replaces Fan Control, MSI
Afterburner / EVGA Precision X1, MSI Center / Dragon Center, ASUS Armoury Crate / AI Suite,
Gigabyte Control Center / AORUS Engine / RGB Fusion, ASRock utilities, SignalRGB, iCUE, and
NZXT CAM for desktop PCs — while staying inside the non-negotiable safety rules in
`AI_CONTEXT.md`. The competitors' documented failures (OpenRGB had to permanently disable its
MSI motherboard RGB code after bricking boards; vendor suites weigh gigabytes, phone home, and
crash) are exactly why the evidence-gated architecture is the winning strategy, not a handicap.

## How RigPilot wins

Vendor suites lose on bloat, telemetry, instability, and lock-in. Community tools lose on
fragmentation (one app per domain) and safety (unsigned drivers, brick incidents). RigPilot's
wedge is being the only suite that is simultaneously:

1. **Complete** — cooling + GPU + CPU + RGB + peripherals + games + monitoring + updates in one app.
2. **Tiny and quiet** — target < 250 MB working set and < 1% CPU (Armoury Crate ~4 GB installed);
   no account, no ads, no cloud dependency, service performs zero network access.
3. **Provably safe** — signed everything, transactional writes with rollback, per-device
   evidence labels, no WinRing0, no bricked boards. Publish the qualification ledger publicly.
4. **Open** — GPL-3.0, signed `.pcha` adapter-pack ecosystem so the community extends device
   support without touching the privileged service.

Every feature below lands at its honest evidence label; ambition raises the amount of
hardware qualified, never the standard of proof.

## Competitor beat matrix

| Competitor | Match (parity) | Beat (their weakness we exploit) |
| --- | --- | --- |
| Fan Control | Curve/graph/trigger/sync/mix editors, calibration, RPM mode, per-output floors, file/plugin sensors | Transactional apply + rollback, stale-sensor fail-safe to max cooling, pump/CPU-fan protection, signed service, AIO + GPU + board fans in one engine |
| MSI Afterburner / Precision X1 | GPU clock/power/VF curve editor, per-GPU fan curves, OC Scanner (driver-integrated via NVAPI), OSD, video capture, profiles + hotkeys | Read-back-verified writes with reset guarantees, no unsigned kernel driver, multi-vendor (NVML/NVAPI + ADLX + IGCL), safe import of existing Afterburner profiles (done) |
| MSI Center / Dragon Center | Board fan control, Mystic Light, game mode, monitoring, update discovery | ~50 MB installer vs GB-class; no telemetry; does not brick RGB (MSI board RGB ships only after per-board qualification — OpenRGB's disabled-code incident is the cautionary tale) |
| Armoury Crate / AI Suite | Aura Sync-class lighting, fan curves ("Fan Xpert" parity via commissioning + calibration), device pages, profile switching, update discovery | 5 MB-class G-Helper ethos at desktop scale: no 4 GB install, no forced services, clean uninstall restoring firmware control |
| Gigabyte Control Center / AORUS / RGB Fusion | Board fan curves, RGB Fusion-class lighting, one-click tune (bounded auto-tune engine exists), monitoring | Evidence-gated auto-tune with WHEA/thermal/display-reset abort + rollback vs their opaque "auto OC"; no ACPI/SMBus conflicts hidden from the user (conflict detection is first-class) |
| SignalRGB / iCUE | Physical layouts, cross-device effects, screen ambience, audio-reactive effects, per-key RGB, macros, game integrations, device packs | Effects run in a sandboxed watchdogged host, device packs are signed and permission-scoped, LGPL/GPL-clean, free forever (SignalRGB paywalls effects) |
| NZXT CAM | AIO pump/fan/LCD control, monitoring, lighting | liquidctl-derived protocols (GPL-3 compatible) inside a crash-contained host; pump writes only after pump-specific qualification — CAM's own overlay/telemetry bloat stays out |
| HWiNFO / HWMonitor | Deep sensor tree, logging, alerts, per-sensor trends, CSV export | Already largely present (SQLite history, aggregates, alerts, comparison charts); add sensor-tree parity view + file/plugin sensor inputs |

## Release train

- **0.5.0-beta — "one system, fully controlled"**: everything the reference system
  (X570-E / 5800X / RTX 3090 / Kraken X3 / Aura board) can safely do, through the service, signed.
- **0.6–0.8 — "the enthusiast stack"**: GPU OC parity with Afterburner (VF editor, OC Scanner),
  CPU tuning (PBO/CO) behind the full qualification gate, AIO control, board-RGB first families,
  peripheral packs, RTSS + capture parity.
- **0.9 — "the ecosystem"**: public adapter-pack SDK + pack signing service, community
  qualification program, localization, portable mode.
- **1.0 — "the replacement"**: 18-system matrix passed, two independent reports per claimed
  write-capable controller family, public compatibility database.

---

## Workstream A — Cooling: beat Fan Control outright

Beta:
- Physical header identification on the X570-E (witnessed pulses, `PhysicalHeaderObserved`),
  full stall/restart characterisation or measured non-stopping floors per header.
- Live cooling-graph runtime through profile transactions (already built in source): activation,
  three-poll stale emergency, slew/floor clamps — supervised live pass per output.
- RTX 3090 fan through the service arm flow (replace standalone-runner evidence); repeated
  cycles + live rollback exercise. Kraken X3 read-only liquid temp + pump RPM via a ported
  liquidctl protocol reader (GPL-3-compatible, attributed) in the contained Adapter Host child.

0.6–0.8:
- Visual node/curve editor parity: graph, linear, trigger, flat, sync, and feedback curve types
  (engine already supports most), drag-editable points, template/duplicate library, per-curve
  preview against recorded traces.
- RPM-target mode (closed-loop RPM hold) for calibrated outputs.
- File- and plugin-sensor inputs (Fan Control's custom-sensor parity) as user-session,
  validated, rate-limited sources that can never command hardware directly — only feed graphs.
- Case-fan zero-RPM with the existing repeated stop/restart verification; pump control unlocked
  only per-controller after pump-specific qualification (Kraken X3 first, via liquidctl-derived
  command set + read-back + failsafe tests).
- Multi-controller support packs: Nuvoton/ITE families via LibreHardwareMonitor + PawnIO 2.2,
  qualified board-by-board through the community program.

## Workstream B — GPU: beat Afterburner / Precision X1

Beta:
- NVML power-limit adapter (100–385 W bounds already discovered) with prepare/apply/verify/
  rollback/reset, arm-gated.
- NVAPI core/memory clock-offset adapter within driver-exposed bounds, Experimental,
  session-only, never persisted at boot. Fix NvAPIWrapper policy read-back first.

0.6–0.8:
- ~~GPU core/memory clock offsets~~ **shipped 2026-07-15** (public-SDK NVAPI pstates20, arm-gated,
  read-only-verified live on the reference RTX 3090: core ±1000 MHz, memory −1000/+3000 MHz).
- **VF curve editor / OC Scanner: DEFERRED BY POLICY (audited 2026-07-15).** The per-point VF
  curve read (`NvAPI_GPU_GetVFPCurve`) and the OC Scanner invocation
  (`ClockClientClkVfCurveScan` family) are NDA/undocumented NVAPI surfaces — NvAPIWrapper marks
  the former `Private*` and does not wrap the scanner at all, and neither appears in the public
  NVAPI SDK. RigPilot's ground rules allow documented vendor APIs only, so these stay out until
  NVIDIA documents them or an equivalent documented path exists. The shipped whole-domain clock
  offsets + power limit cover the practical OC workflow; the scanner's one-click role is served
  by the screening pipeline over user-chosen offsets instead.
- Per-GPU fan curve binding into the cooling graph engine (GPU temp → GPU fan, hotspot-aware).
- **AMD**: ADLX adapter (official MIT SDK; C# bindings per AMD's SWIG guide or Rem0o/epinter
  wrappers as references) for fan/power/clock on RDNA cards — built against fakes now, qualified
  when AMD hardware enters the lab/community program.
- **Intel**: IGCL telemetry + `ctlOverclock*` frequency/power/temperature-limit adapter
  (official MIT repo, 64-bit); presence detector already ships; qualified on first Arc system.
- Multi-GPU: per-adapter instances keyed by exact PCI identity (architecture already per-device).

## Workstream C — CPU: careful but real

Beta:
- ~~PawnIO 2.2 signed + RyzenSMU module (Zen 1–4 incl. 5800X): read-only SMU telemetry~~
  **live 2026-07-15**: the user installed the signed PawnIO driver, and LibreHardwareMonitorLib
  0.9.6 (already shipped) loads its embedded `RyzenSMU.bin`/`AMDFamily17.bin` modules through it.
  Verified on the deployed beta3 service: package power, per-core SMU power, Tctl/Tdie + CCD1
  Tdie, SVI2 TFN core/SoC voltages, per-core VIDs and effective clocks, all quality=Good.
  CPU/SMU writes remain Blocked as before — telemetry only.

0.6–0.8:
- Ryzen PBO limits (PPT/TDC/EDC) as the first CPU write family: bounded, vendor-documented
  ranges, mailbox read-back, guaranteed stock reset, boot-recovery revert sentinel — the full
  gate from `docs/qualification/cpu-tuning-and-intel-arc.md`.
- Curve Optimizer per-core offsets only after per-family mailbox bounds + applied-curve
  read-back are proven (this is voltage-adjacent: Manual Only + per-session acknowledgement,
  never automatic, never at boot until three clean cold boots).
- Intel: Core/Core Ultra power-limit (PL1/PL2) via documented MSR/MMIO through a signed PawnIO
  module — same gate. No undervolting until read-back/reset proof.
- Windows power plan deep controls (boost mode, per-plan processor limits) — extends the one
  already-Verified adapter, cheap wins.

## Workstream D — Lighting: beat Armoury Crate / RGB Fusion / SignalRGB

Beta:
- OpenRGB bridge bumped to the 1.0 protocol (v4 plugin/SDK API), physical pass against real
  controllers; Windows Dynamic Lighting physical LampArray pass.
- USB/HID peripheral inventory re-enabled everywhere via the crash-contained discovery child.

0.6–0.8:
- **Native board RGB, one family at a time**: ASUS Aura SMBus first (this board), then ASRock
  Polychrome (SMBus), then Gigabyte RGB Fusion — each as an Adapter Host adapter with
  containment, static-scene read-back, reset, unplug, and recovery evidence. MSI Mystic Light
  boards ship **last and only with per-board allowlists** (the OpenRGB brick incident is the
  documented reason). License note: OpenRGB is GPL-2.0 — treat as protocol documentation and
  clean-room reference unless license compatibility is verified; liquidctl (GPL-3) may be
  ported directly with attribution.
- Effects parity: the sandboxed Effect Host already runs hash-bound JS effects with watchdog;
  add the stock effect library (wave/breathe/audio-reactive/temperature/screen-ambient), zone
  and per-key layouts, and effect→scene→profile binding per game.
- Screen ambience (monitor-edge sampling → LEDs) in the user session with explicit capture
  consent, frame-rate capped.
- Peripheral RGB via signed device packs (keyboards/mice/headsets/DRAM/strips), each pack
  permission-scoped to exact VID/PID.

## Workstream E — Peripherals & devices: beat iCUE/CAM device pages

0.6–0.9:
- Signed `.pcha` device packs gain HID write capability classes: DPI stages, polling rate,
  onboard profile slots, battery/charge telemetry, headset sidetone — one exact device at a
  time, contained, with read-back where the protocol allows.
- AIO/LCD: Kraken X3 pump curve (post-qualification) and LCD image/telemetry push via
  liquidctl-derived protocol; same pattern for one Corsair and one Lian Li device as the
  template packs.
- Monitor control beyond brightness: contrast/input-switch/volume via the existing DDC/CI
  path (VCP codes), same confirm + read-back model.
- Keyboard macro passthrough stays user-session only (existing visible recorder), never in
  the privileged service.

## Workstream F — Monitoring, OSD, capture: beat CAM/Afterburner overlays

Beta:
- ScreenRecorderLib (MIT) WGC + Media Foundation H.264/WASAPI recording behind picker consent.
- RTSS shared-memory client: publish RigPilot's curated sensor line into the RTSS OSD (the
  overlay gamers already trust); RigPilot's own non-injecting OSD stays default.

0.6–0.8:
- Frametime/FPS statistics read from RTSS shared memory (1%/0.1% lows) into monitoring +
  per-game session summaries; benchmark-run capture (start/stop, CSV/JSON export).
- HWiNFO-class sensor tree view with per-sensor min/avg/max, logging profiles, and alert rules
  (engine exists — surface it fully).
- Replay-buffer capture (last N seconds) once the encoder path is proven.

## Workstream G — Games & automation

0.6–0.8:
- GOG/Xbox/Battle.net manifest coverage added to Steam/Epic scanning; artwork via local files
  only (no metadata download, per privacy rule).
- Game mode: foreground-game trigger applies the per-game bundle (profile + scene + macro +
  OSD + capture preset) — automation engine already supports this; add the one-toggle UX.
- Hotkey overlay palette (switch profile/curve/scene from in-game OSD).

## Workstream H — Updates & system care: beat vendor-suite updaters

0.8–1.0:
- Driver update discovery per exact PnP device against vendor catalogs (user-process network
  only), staged-INF validated executor (built) exercised on real packages, OEM rollback export.
- BIOS/firmware: discovery + download + integrity check + hand-off to the vendor's own flasher
  with BitLocker guidance. RigPilot never flashes firmware itself (permanent rule).
- Clean-uninstall guarantee test suite: firmware control restored, shared PawnIO preserved.

## Workstream I — Platform, ecosystem, distribution

Beta:
- SignPath Foundation (free OSS OV signing, HSM-held) or Azure Artifact Signing ($9.99/mo)
  wired into `publish.ps1`/`build-installer.ps1`; signed alpha → beta pipeline, signed Game Bar
  MSIX, signed takeover-executor live tests.
- Deploy `report-api` (Cloudflare D1/R2, 30-day lifecycle); opt-in diagnostics uploads live.
- Close the 24-hour soak; working-set reduction pass (target < 300 MB beta, < 250 MB by 1.0).

0.9–1.0:
- **Adapter-pack SDK**: public docs + templates + a pack-signing flow (enroll the production
  Ed25519 publisher key; community packs signed after automated containment tests) — this is
  the moat none of the vendor suites can copy.
- **Community qualification program**: in-app "contribute evidence" flow producing signed
  `HardwareQualificationRecordV1` reports; public ledger site showing per-device status —
  crowdsources the 18-system matrix and becomes the public compatibility database.
- Winget/Scoop/GitHub Releases distribution; portable (no-service, read-only) mode; localization
  (resx extraction, top 8 languages); accessibility audit completion (screen reader/keyboard).
- Auto-update for RigPilot itself: signed, delta, user-process download + service-verified swap.

---

## Hardware acquisition & qualification reality

Beating vendor suites requires their breadth. Bridge the gap three ways: (1) the community
evidence program above, (2) a small physical lab priority list — one AMD RDNA GPU, one Intel
Arc GPU, one Intel 12th+ system, one each MSI/Gigabyte/ASRock board, one Corsair + one Lian Li
ecosystem device — acquired used/cheap in priority order, (3) per-family read-only support
shipped broadly (inventory + telemetry work everywhere today) so users see value before their
write path is qualified.

## Sequencing

```
Beta (0.5): A+B(beta items) + C(telemetry) + D(bridges) + F(capture/RTSS) + I(signing/report-api)
0.6: GPU clock offsets (shipped; VF editor/OC Scanner deferred by documented-API policy) · PBO limits · Aura SMBus native · frametime stats (shipped) · game mode
0.7: AIO pump (Kraken) · Polychrome · peripheral packs v1 · sensor-tree parity · GOG/Xbox
0.8: ADLX/IGCL on real hardware · Curve Optimizer (gated) · RGB Fusion · driver updater live
0.9: pack SDK + signing service · community ledger · localization · portable mode
1.0: 18-system matrix + two independent reports per write family · public database
```

Signing and the report-api have external lead time — start immediately. The community
qualification program should launch with the first public beta to start accumulating evidence.

## Status ledger — 2026-07-16

Everything buildable and verifiable from code on the reference machine is built. Shipped and
software-verified since 2026-07-15 (each with tests and an AI_CONTEXT snapshot): GPU fan / power
limit / core+memory clock offsets (all arm-gated, disarmed by default, live capability cards
verified on the deployed beta3 service), WGC H.264 recording, explicit PNG snapshots, RTSS OSD
bridge (publish + frame stats + benchmark sessions), Kraken X3 read-only telemetry (device
detected live; CAM held the stream — designed refusal), Ryzen SMU telemetry live via PawnIO +
LHM 0.9.6, SMU PBO feasibility reader (read-class-only invariant pinned by test), and the
frame-rate benchmark. Suite: 475 tests, 0 warnings; payload `0.4.0-alpha-20260716-beta5`
validated; no vulnerable packages.

`Test-ReleaseReadiness.ps1` (run 2026-07-16) states the remaining 1.0 gap precisely, and none of
it is code: **(1) code signing** (no certificate enrolled — SignPath Foundation or Azure Artifact
Signing decision pending), **(2) the 18-physical-system qualification matrix** (0 of 18 signed
system records; every CPU/GPU/board family row unpopulated — needs the community program plus
the lab list above), **(3) user-witnessed live passes** on this reference system (Kraken read
with CAM closed, arm-gated GPU fan/power/clock writes, recording, RTSS publish/benchmark, SMU
feasibility via the deployed service, 24 h soak). Fabricating any of these records is
prohibited; 1.0 waits for the evidence, exactly as this document intended.

### Addendum — 2026-07-16 (afternoon): beta5 deployed; Phase-4/0.9 software line shipped

beta5 was user-deployed and verified live (runtime-preflight Ready, all four GPU control cards
correctly ReadOnly while disarmed) and the **first LocalSystem SMU evidence** was captured on
the 5800X (SMU fw 56.76.0, PM table 0x380905: PPT 70.2/160 W, TDC 29.7/110 A, EDC 148.9/160 A,
THM 53.6/90 °C — read-only qualification evidence). Then the remaining no-hardware roadmap items
shipped (each tested; suite now **517 tests, 0 warnings**; payload `0.4.0-alpha-20260716-beta6`
validated, 6 components, protocol 2):

- **CPU PBO tuning scaffolding behind the full gate** (gate doc steps 2–4): `ISmuTuningTransport`
  seam with fake-only transports, `AmdSmuTuningAdapter` double-gated (qualification witness AND
  acknowledged arm — Blocked on every system today), `CpuTuneBootSentinel` boot-recovery journal
  (durable before any write; unclean boot restores stock; recovery wired at service start), and
  `SetCpuTuningArmed` IPC/CLI that refuses with CPU_TUNING_QUALIFICATION_REQUIRED. No SMU write
  transport exists anywhere in the repository.
- **AMD ADLX read-only feasibility detector** (`amd.adlx`, presence-only, silent on this NVIDIA
  rig) mirroring the IGCL detector; full ADLX/IGCL telemetry still needs real Radeon/Arc hardware.
- **Adapter-pack SDK**: docs/adapter-pack-sdk.md (exact verifier limits, manifest schema, trust
  routes), docs/sdk-template/, scripts/New-AdapterPack.ps1 — template pack built and verified
  against the live beta5 verifier (passes all structural checks; fails only signature/trust, as
  designed).
- **Community qualification evidence flow**: `pchelper-cli qualification-draft` builds UNSIGNED
  DRAFT HardwareQualificationRecordV1 entries from the real local identity; requires all four
  witnessed attestations + --confirm-witnessed (refusal verified live); drafts hard-code
  signedProductionBuild=false so they can never satisfy the 18-system gate.
- **Portable mode**: `PCHelper.App --portable` runs without the service or any pipe server —
  read-only local probe, writes locked, distinct "Portable (read-only)" labelling (rendered and
  verified via the snapshot host).
- **Localization scaffolding**: Strings.resx pipeline + L10n/`{loc:Loc}` + `--culture` switch +
  per-key English fallback, German satellite as reference, portable-mode strings extracted first;
  workflow in docs/localization.md.

Still hardware/user/policy-gated (unchanged): signing, the 18-system matrix, witnessed passes,
Aura SMBus native writes, Polychrome/RGB Fusion, peripheral packs, AIO pump control, ADLX/IGCL
telemetry on real hardware, live PBO/CO writes, auto-update delivery, report-api deployment.

## Risks

- Scope: each workstream lands per-device/per-family; a missed qualification ships as honest
  `ReadOnly`/`Experimental`, never slips the train.
- MSI board RGB is genuinely dangerous (documented bricks) — per-board allowlist, last.
- OpenRGB GPL-2.0 vs GPL-3.0 compatibility must be legally checked before any code reuse;
  default is clean-room protocol notes.
- OC Scanner/NVAPI behaviours change across driver branches — pin tested driver ranges per
  feature and fail closed.
- Community packs are a supply-chain surface — signing + automated containment tests +
  permission scoping are mandatory, no exceptions.

## Permanent non-goals

No WinRing0. No firmware flashing by RigPilot. No RAM timing/voltage writes. No automatic
voltage increases. No telemetry without explicit opt-in. No terminating competitors by name.
No capability label widened for marketing.

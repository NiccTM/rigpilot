# Library & SDK due-diligence — 2026-07-15

Open-source / SDK integration research for RigPilot, vetted against the non-negotiable
safety constraints (no WinRing0/vulnerable drivers, GPL-3.0-distribution-compatible
licensing, .NET 10 C# interop, gated writes with read-back/reset/boot-recovery, and no
network I/O in the LocalSystem service).

Headline: the two biggest wins are (1) **more read-only adapters on the PawnIO modules
whose signed driver is already shipped** and (2) a small set of **permissive .NET
libraries** for OSD, capture, and HID. Most vendor GPU/CPU *tuning* SDKs carry a license
or hardware-test blocker; Intel CPU undervolt is largely **infeasible** under VBS/HVCI.

## A. CPU tuning (AMD Zen + Intel)

- **PawnIO.Modules** — https://github.com/namazso/PawnIO.Modules — **GPL-2.0-or-later**,
  signed PawnIO driver — **PASS**. Beyond the `RyzenSMU.p` already used, it ships 13
  modules: `IntelMsr` (Intel CPU MSR), SMBus (`SmbusI801`, AMD PIIX4, Skylake IMC),
  `LpcIO`/Super-I/O, and an EC module. Unlocks read-only Intel telemetry, **SMBus access
  without WinRing0** (RGB/DIMM/sensor), and Super-I/O fan/temperature reads. Effort S–M
  per module via the existing PawnIO ioctl path. Ryzen/CPU writes stay BLOCKED.
- **Intel CPU undervolt (MSR 0x150)** — **DISQUALIFIED on target**. Writes are blocked by
  VBS/HVCI on secured Windows; the only modern path is a pre-boot EFI driver
  (https://github.com/Victor857/Undervolt, MIT) — out of scope for a signed Windows
  service and hostile to boot-recovery.
- **ZenStates-Core** — WinRing0 → **DISQUALIFIED**. Use `ryzen_smu` (Linux) + ZenStates
  only as mailbox-spec references for the future gated Ryzen tuner.

## B. GPU control / telemetry

- **AMD ADLX** — https://github.com/GPUOpen-LibrariesAndSDKs/ADLX — license is a custom
  **"ADLX SDK License Agreement.pdf"**, NOT a standard OSI/MIT license. **Needs a GPL-3.0
  compatibility review before integration.** C# via SWIG-generated bindings. Read-only
  telemetry + tuning with read-back. Build the read-only feasibility detector (mirror
  `IntelGraphicsControlAdapter`) but do not link ADLX until the license clears and an AMD
  GPU is available. Effort M.
- **Intel IGCL** — https://github.com/intel/drivers.gpu.control-library — MIT headers, no
  official C# wrapper (P/Invoke `ctlInit`, version-tagged structs, 64-bit). Finish on an
  Arc system. Effort M–L.
- **NVAPI/NVML (already integrated)** — `NvAPIWrapper` (LGPL-3.0) already exposes
  clock/power/VF read-back; NVML read-only. No new lib needed to read applied GPU-OC state.

## C. AIO / USB coolers

- **liquidctl** — https://github.com/liquidctl/liquidctl — **GPLv3** (compatible) but
  **Python**: not linkable into the C# service. Value is the documented per-device HID
  protocols (pump/fan duty + RPM read + safe defaults) to **clean-room** a contained HID
  transport, or shell out to `liquidctl.exe` (see
  https://github.com/jmarucha/FanControl.Liquidctl). Keep pump zero-RPM BLOCKED; run behind
  the Adapter Host. Effort L; risk: per-device protocol variance, native crash containment.

## D. RGB / lighting (beyond OpenRGB)

- **RGB.NET** — https://github.com/DarthAffe/RGB.NET — **LGPL-2.1-only** (compatible),
  C#/.NET native. Several providers wrap proprietary vendor daemons (iCUE/Aura) that must
  run — not offline. Use only the open-protocol providers; treat SDK-backed ones as
  inventory-only. Effort M.
- **Razer Chroma / Corsair iCUE SDKs** — proprietary, require the vendor daemon running,
  restricted redistribution → **DISQUALIFIED** for an offline GPL-3 suite. OpenRGB
  (already integrated) remains the best offline route.

## E. Peripherals (HID)

- **HidSharp (IntergatedCircuits fork)** — https://github.com/IntergatedCircuits/HidSharp
  — **Apache-2.0** (compatible), maintained. Adds HID-level detail (usage pages, product
  strings, LampArray/keyboard/mouse/AIO-LCD classification) beyond the current WMI/PnP
  inventory. HID enumeration should still run behind the Adapter Host (native crash
  containment). Effort S–M. `Hid.Net`/`Device.Net` (MIT) is a viable alternative.

## F. OSD + capture

- **RTSSSharedMemoryNET** — https://github.com/spencerhakim/RTSSSharedMemoryNET —
  **LGPL-3.0** (compatible), C#. Reads RTSS data and writes OSD text via shared memory —
  **no injection** (RTSS hooks). Best fit for read-only-first OSD (user agent). Effort S.
- **ScreenCapture.NET** (DarthAffe) — MIT, DXGI Desktop Duplication, no injection; best for
  still snapshots. **ScreenRecorderLib** (sskodje) — MIT, Media Foundation + WGC, still +
  video. **Windows.Graphics.Capture** via CsWinRT — native OS API, consent-gated.

## G. Driver / firmware update safety — no third-party library

WinVerifyTrust/WTHelper (Win32, already used in the takeover signer path), PnPUtil
(built-in), and BitLocker via WMI `Win32_EncryptableVolume` (built-in). Nothing to
integrate — it is OS surface.

## H. Hardware-in-the-loop testing — no framework; current pattern is correct

No standard HIL/fake-driver framework exists. The injectable-transport + fake-adapter
pattern already used across RigPilot is the right approach. Optionally build a
`SensorSample` record/replay harness for tests.

## Recommended sequencing

- **Now (safe, no new hardware):** PawnIO transport feasibility detector (SMBus /
  Super-I/O / Intel-MSR module presence) → PawnIO read adapters; HidSharp inventory; RTSS
  OSD read.
- **Gated (needs matching hardware to finish):** ADLX read-only detector (after license
  review), IGCL telemetry, liquidctl-derived AIO fan transport (pump blocked), Ryzen SMU
  tuner + boot-recovery.
- **Don't:** Intel MSR undervolt (VBS-blocked), Chroma/iCUE SDKs (proprietary/online),
  ZenStates-Core (WinRing0), ADLX until its SDK license clears GPL-3.

# Competitor parity ledger

This is a clean-room behaviour comparison, not a branding, binary, protocol, or device-definition copy. A green source feature does **not** mean a controller is qualified for writes on a given PC.

## Evidence terms

| Label | Meaning |
| --- | --- |
| Implemented | Source and automated coverage exist. |
| Partial | A safe user experience or contract exists, but an adapter, device test, or release prerequisite is missing. |
| Blocked | The feature would need an unqualified write path, vendor licence review, signing, or physical evidence. |

## What the competitors actually set as the bar

| Competitor | Documented desktop-relevant behaviour | RigPilot status | Exact gap / constraint |
| --- | --- | --- | --- |
| [Fan Control](https://getfancontrol.com/docs/) | Pair fan controls to RPM sensors; calibrate duty-to-RPM response; manual and curve control; RPM mode; graph/linear/trigger/flat/sync/feedback curves; mixed and averaged sensors; hysteresis and response time. | **Partial** | The graph engine, calibration, nonzero-floor protection, mixed sensors, hysteresis, slew, imported graphs, and new manual curve studio exist. The studio saves an inactive temperature-to-duty graph only after a physically observed, calibrated output. Full visual node editing, custom file-sensor authoring, and qualified live use still remain. |
| [G-Helper](https://g-helper.com/) | Low-friction profile switching, fan curves, monitoring/overlay, hotkeys, automation, vendor-specific tuning, and model-specific driver/firmware discovery. | **Partial** | RigPilot has stock-safe profiles, local automation, tray operation, monitoring, local OSD, and calibration-bound curves. Laptop-only controls are excluded. Desktop CPU/GPU write adapters and update execution need exact-device evidence. |
| [MSI Afterburner](https://www.msi.com/Landing/afterburner/graphics-cards) / [EVGA Precision X1](https://www.evga.com/precisionx1/) | GPU telemetry, custom GPU fan curves, power/clock/voltage-frequency controls, OC Scanner, OSD, and capture. | **Partial** | RigPilot imports Afterburner profiles safely and has OSD/snapshot foundations, but RTX 3090 clock, power, VF, OC Scanner, RTSS output, and video capture are not exposed. No UI setting is substituted for missing NVAPI/NVML write/read-back/reset proof. |
| [Armoury Crate](https://dlcdnets.asus.com/pub/ASUS/mb/14Utilities/E15698_Armoury_Crate_FAQ_QSG_WEB.pdf?model=armoury+crate) | ASUS device modes, Aura Sync, fan control, device pages, and model-specific updates. | **Partial** | RigPilot detects ownership conflicts, models profiles, controls qualified motherboard outputs, and bridges OpenRGB/Dynamic Lighting. Aura, USB peripherals, vendor updates, and firmware execution remain blocked until exact controller containment, signing, and reset evidence pass. |
| [SignalRGB](https://docs.signalrgb.com/) | Physical RGB layouts, cross-device effects, screen ambience, macros, game effects, and device plug-ins. | **Partial** | Physical layouts, declarative effects, isolated effect host, macro/game foundations, OpenRGB bridge, Dynamic Lighting, and a conflict-aware per-endpoint RGB routing matrix exist. Direct USB/HID device packs remain read-only; RigPilot does not load arbitrary third-party device plug-ins into the privileged service. |
| [Corsair iCUE](https://www.corsair.com/us/en/explorer/diy-builder/how-tos/how-to-create-a-custom-fan-curve-in-icue/) | Per-device Quiet/Balanced presets, fixed %, fixed RPM, custom curves, sensor selection, lighting layout, and alerts. | **Partial** | RigPilot has profile/health-rule foundations and calibration-bound manual curves with a fixed thermal ceiling. It does not emulate Corsair controller protocols or write hardware lighting/onboard profiles without a reviewed adapter. |
| [NZXT CAM](https://support.nzxt.com/) | Device monitoring, AIO pump/fan controls, lighting, and hardware updates. | **Partial** | Inventory and safety-role handling exist. The detected AIO pump remains protected; no direct NZXT USB protocol, pump writer, or firmware executor is claimed. |
| [OpenRGB SDK](https://openrgb.org/sdk.html) / [Windows Dynamic Lighting](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/lighting-dynamic-lamparray) | Cross-device lighting through a local network bridge or HID LampArray, respectively. | **Implemented bridge / Partial device support** | RigPilot routes each endpoint through Windows Dynamic Lighting, an explicitly enabled local OpenRGB server, a verified adapter, or read-only qualification. Actual device coverage depends on the installed server, LampArray compatibility, and an available ownership lease; a manufacturer-name match never enables a proprietary USB write. |

## Parity added in this slice

The **Manual curve studio** moves the cooling UI closer to Fan Control and iCUE without inventing hardware support:

- accepts two to eight explicit `temperature:duty` points;
- uses the existing maximum-of-current-CPU-and-GPU-temperature source selection;
- preserves the exact output's calibrated nonzero floor and controller maximum;
- requires the final point to reach the controller maximum;
- supports independent rise/fall hysteresis and response times;
- creates a typed graph and inactive profile only; normal profile confirmation is still required to apply it;
- requires a completed, physically observed commissioning report and matching calibration.

It deliberately does **not** add fan stop, arbitrary command execution, or an unqualified live write path.

## Ordered work to close real gaps

1. **Cooling operator UX:** add a graph-point editor/visual preview, duplicate/template library, and RPM-output authoring only for outputs with a valid full calibration. Test with fake traces first, then one physically mapped chassis header.
2. **Qualified reference adapters:** validate Ryzen 5800X PPT/TDC/EDC read-back/reset, then RTX 3090 power/clock only after driver-gated apply/read-back/reset evidence. Do not expose generation-wide switches.
3. **Lighting qualification:** qualify one controller at a time through Dynamic Lighting or an isolated Adapter Host; begin with a static scene and reset test, not an effects library.
4. **OSD/capture:** complete an optional RTSS bridge and Windows Graphics Capture/Media Foundation/WASAPI backend behind explicit consent and failure recovery.
5. **Device software parity:** add direct USB, LCD, DPI, polling, macro, and hardware-profile support only as reviewed, signed, per-controller packs.
6. **Updates:** retain exact vendor package validation and add a signed, physically tested driver updater before attempting any firmware workflow.

## Non-goals that prevent false parity

- No generic motherboard BIOS, EC, or GPU firmware flasher.
- No WinRing0, security bypass, or instruction to disable Windows protections.
- No production takeover of competing control software without a signed service, stored exact-binary consent, default-reset proof, and physical validation.
- No claim of replacing Fan Control, Afterburner, SignalRGB, Armoury Crate, iCUE, or CAM until the relevant controller family passes the qualification ledger.

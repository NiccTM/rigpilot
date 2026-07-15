# Compatibility

This file records evidence, not marketing claims.

## Development reference system

| Component | Detected reference |
| --- | --- |
| Operating system | Windows 11 x64, build 26200 |
| Motherboard | ASUS ROG Strix X570-E Gaming, BIOS 5031 |
| CPU | AMD Ryzen 7 5800X |
| GPU | NVIDIA GeForce RTX 3090, `PCI\VEN_10DE&DEV_2204&SUBSYS_161319DA&REV_A1`, driver 32.0.16.1062 |

On 12 July 2026, the reference system completed two controlled live write passes. Fan Control 271 and MSI Afterburner 4.6.6 were signature/hash validated, stopped by exact executable identity, and restored after each pass. RigPilot (then named PC Helper) remains Experimental for these devices because one system is insufficient for certification and several safety, restart, suspend, reboot, and uninstall requirements remain open.

## Current reference evidence

| Surface | Exact evidence | State |
| --- | --- | --- |
| Windows active power scheme | In both passes, custom scheme `8cdab5a0-ac8c-4ae8-ba68-990b7891db0a` changed to Balanced, read back, and rolled back to the exact original GUID | Repeated live apply/read-back/rollback passed on this reference system |
| NVIDIA RTX 3090 fan channels | Both 30-100% channels passed software read-back, physical RPM response, 15-point calibration, rollback, and default reset twice. High-duty readings were 2,172/2,182 RPM then 2,238/2,241 RPM. Later restart qualification exposed low-duty hysteresis: a candidate could start once but fail a repeated cycle, and the first observed stall duty was not always a reliable stop command after spin-up. The corrected engine searches higher candidates from the lowest confirmed stop duty under continuous thermal limits. Physical verification still aborted safely when GPU Hot Spot reached 85.2 °C. | Experimental single-system evidence; zero-RPM remains disabled and the deterministic unverified floor is 50%; normally Blocked while Fan Control or MSI Afterburner runs |
| Nuvoton NCT6798D motherboard fan channels | All seven controls accepted default reset in both passes. Fans 1-3 responded at 100%: 1,562.5 to 1,936.9, 1,513.5 to 1,909.5, and 1,179.0 to 1,532.3 RPM in pass 1; 1,702.4 to 1,956.5, 1,648.4 to 1,906.8, and 1,296.8 to 1,534.1 RPM in pass 2. Fan 5 was already at 100%. Fans 4, 6, and 7 had no RPM response and received reset only. | Experimental repeated high-duty/reset evidence only; header identity and low-duty calibration remain unqualified |
| NVIDIA NVML public bound discovery | Local read-only probe loaded NVML 610.62, enumerated two fan targets and a 100-385 W power-limit range on the exact RTX 3090. The adapter issued no setters. | ReadOnly; evidence supports monitoring and boundary display only |
| Nuvoton Fan #1 alpha commissioning | On 14 July 2026, the UAC-staged 0.4 alpha service used `lhm.control:/lpc/nct6798d/0/control/0` with paired RPM sensor `lhm.sensor:/lpc/nct6798d/0/fan/0`. A 60% two-second pulse completed Prepare, Apply, Verify, and ResetToDefault. A later 100%-to-0% bounded calibration completed with stable samples, 1959.36 RPM maximum, 45.5 C maximum controller temperature under a 70 C ceiling, and successful rollback. The tach was about 546 RPM at requested 0%, so the sweep found no stop/restart point. | Experimental completed non-stall characterisation only. The persisted `CASE_FAN_1` alias is not physical-header proof. No curve, zero-RPM policy, or general Nuvoton write claim is enabled. Fan #5 is persisted as `Pump` / `AIO_PUMP` and remains protected. |
| CPU/GPU clock, power, voltage-frequency writes | No reviewed vendor/reverse-engineered write endpoint is present. NVML bounds do not qualify a write path. | Unsupported |
| Windows driver update executor | Source and fake-package integration tests validate staged-INF containment, exact device/catalog/publisher/hash checks, OEM rollback export, signed-service gating, reboot journalling, and `PnPUtil` invocation. No vendor package has been installed on this system. | Software-qualified only; no live driver-update claim |
| Read-only compatibility catalogue | A local source probe recognised the exact ASUS X570-E, Ryzen 7 5800X as Ryzen/Threadripper 5000 (Zen 3), RTX 3090 as RTX 30 series, Aura controller, and HyperX USB controller. The catalogue is exercised with mainstream AMD/Intel/NVIDIA/board/peripheral identity tests. | ReadOnly identity evidence only; no write capability is inferred |
| OpenRGB | Loopback SDK protocol path is integration-tested against a bounded fake server | Implemented bridge; no physical controller qualification claim |
| Windows Dynamic Lighting | LampArray discovery, brightness, enable/disable, and per-index zone code compiles against the installed Windows SDK | Implemented bridge; no physical controller qualification claim |
| Adapter packs and importers | Ed25519/hash/archive tests plus representative Afterburner and Fan Control format tests | Software-qualified only; no adapter-pack publisher key or imported hardware write is qualified |

> **Nuvoton evidence boundary:** historical repeated-pass and 14 July commissioning rows are single-system alpha evidence only. The successful alpha service operation supersedes the earlier no-write execution-context preflight, but it does not establish physical header identity, a stop/restart point, live stale-sensor timing, suspend/resume, reboot, uninstall, or multi-system qualification. A future general write claim still requires that evidence.

The Adapter Host disables LibreHardwareMonitor USB-controller discovery. Its HidSharp native-load failure was observed to terminate the child process; unsupported USB/AIO controllers remain unavailable until that path is packaged and fault-tested. Current hosts use an authenticated private pipe and a kill-on-close job object, but containment does not qualify a hardware endpoint.

## Read-only family recognition

RigPilot now recognises Ryzen/Threadripper 1000-9000, Intel Core 6th-14th/Core Ultra, GTX 700-16, RTX 20-50/professional, Radeon RX 400-9000/professional, Arc A/B/professional, additional desktop-board vendors, HID LampArray, and more cooling-controller/peripheral families from Windows identity strings. This improves inventory and eligibility diagnostics only; it does not widen any hardware write claim. The exact recognised families and evidence boundary are in [docs/compatibility-catalog.md](docs/compatibility-catalog.md).

Fault injection verified Adapter Host respawn. The first service-kill test exposed missing Windows recovery actions; MSI 0.3.3 now installs three five-second restart actions and recovered automatically. Event review also exposed an unhandled pipe-write cancellation when a client disconnected; `0.3.0-alpha.4` contains the fix, a regression test, and an installed recovery retest with no new .NET Runtime or Application Error crash. A privileged ten-minute closed-dashboard soak measured 0.458% combined CPU, 394.1 to 388.1 MB working set, no growth trend, and zero unexpected TCP connections. The CPU and network targets passed; the 200 MB working-set target failed.

No WHEA, display-reset, Kernel-Power 41, or unexpected-shutdown event was recorded during either controlled pass. This does not substitute for cold-boot, suspend/resume, GPU-driver-restart, or 24-hour soak testing.

The later restart-verification work added stable median windows, consecutive running/stopped confirmation, two or three complete restart cycles, candidate promotion after a failed repeated start, hysteresis-aware stop duty, and sensor-specific thermal ceilings. Those paths pass deterministic tests, but the physical RTX 3090 did not complete the full sequence inside the selected safety ceilings. RigPilot therefore does not claim restart verification on this card.

## Evidence levels

- **Verified:** exact device/controller family passed read-back, apply, verification, reset, fault, reboot, and soak checks on at least two independent systems.
- **Experimental:** bounded write path exists but has incomplete qualification.
- **ReadOnly:** monitoring is available; writes are intentionally unavailable.
- **Blocked:** another controller, missing privilege, driver policy, or safety condition prevents ownership.
- **Unsupported:** no adapter exposes the capability.

No CPU, GPU, fan, AIO, or RGB write capability is currently certified. `Experimental` means code exists behind acknowledgements; it does not mean stable or broadly supported.

The machine-level release ledger is in [docs/qualification](docs/qualification/README.md). Its current reference record is intentionally unsigned and incomplete, so it does not count toward the signed 18-system version-1 gate.

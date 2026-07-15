# Full desktop-suite plan audit

This is an implementation audit of the supplied **PC Helper Full Desktop Control Suite** plan. It separates source implementation, test evidence, and physical qualification. A source feature is not a claim that a device is safe to write.

## Status definitions

| Status | Meaning |
| --- | --- |
| Implemented | The feature exists in source and has automated coverage. |
| Partial | A safe foundation exists, but a required backend, physical test, or release prerequisite is missing. |
| Blocked | The feature must remain unavailable until an external prerequisite is met. |

## Architecture and safety

| Plan area | Status | Evidence and remaining boundary |
| --- | --- | --- |
| Versioned service, user-agent, and Adapter Host pipes | Implemented | Protocol v2, request identity/revision checks, private Adapter Host pipes, and job-object containment are in source and tests. |
| Typed profiles, migration, transactions, rollback | Implemented | V1/V2 migration, ordered typed transactions, journal, verification, and rollback are covered by core tests. |
| Signed `.pcha` adapter packs | Partial | Archive, hash, Ed25519, permission, protocol, and developer hash checks exist. Production publisher-key enrolment and signed production packs are not complete. |
| Exact-identity competitor takeover | Partial | Consent, identity revalidation, rollback, default-reset gating, and give-back are implemented. Unsigned service images hard-block mutation; signed physical execution is missing. |
| Security boundaries | Implemented | No WinRing0 path, no security bypass, no service scripts, and no generic firmware writer. |

## Control, cooling, and tuning

| Plan area | Status | Evidence and remaining boundary |
| --- | --- | --- |
| Fan graphs, calibration, commissioning, emergency fallback | Implemented / Experimental hardware | Graph nodes, calibration, restart logic, stale-source protection, typed commissioning, rollback, and tests exist. Advanced Lab offers a calibration-bound manual temperature:duty curve studio with independent rise/fall hysteresis/response settings and a mandatory full-speed ceiling. Exact header mapping, long-duration restart, suspend/resume, reboot, uninstall, and multi-system evidence remain open. |
| Stock-safe Windows power profiles | Implemented | Discovery, apply, read-back, rollback, generated Quiet/Balanced/Performance/Efficiency profiles, and tests exist. |
| ASUS X570-E / Nuvoton writes | Partial | Controlled reference-system fan evidence exists, but certification requirements are incomplete. |
| Ryzen / Intel CPU tuning | Blocked | No audited SMU/PawnIO, XTU, or MSR write adapter with exact bounds, read-back, reset, crash recovery, and physical qualification exists. |
| NVIDIA RTX tuning | Partial | NVML telemetry and bounded fan/power/VF discovery are read-only. The RTX 3090 restart floor remains 50%; no clock, power, thermal, voltage-frequency, or OC Scanner setter is exposed. |
| AMD ADLX / Intel IGCL writes | Blocked | Runtime eligibility detection exists; no write-capable adapter is present. |
| Automatic tuning | Implemented engine / Blocked endpoints | Single-domain search, voltage exclusion, rejection conditions, screening, provisional state, and boot sentinel are implemented. No CPU/GPU endpoint is eligible. |

## Lighting, peripherals, games, and automation

| Plan area | Status | Evidence and remaining boundary |
| --- | --- | --- |
| OpenRGB, Dynamic Lighting, layouts, declarative effects | Partial | Safe user-session bridges, layout editing, effect engine, and isolated effect host exist. Physical controller qualification remains open. |
| Direct RGB/peripheral packs | Blocked | USB/HID inventory is read-only. No direct protocol, DPI, polling-rate, button-map, LCD, or onboard-profile writer is exposed. |
| Games, workflows, macros, trusted scripts | Implemented foundation | Offline local store scanning, typed bundles, visible macro record/edit/test, playback, hash-bound scripts, and user-agent isolation exist. |
| Game Bar | Partial | Read-only user-agent bridge and signed-package build path exist. Production certificate, install, package-SID, and activation validation are open. |
| Local desktop OSD and explicit PNG snapshots | Implemented alpha | A non-injecting WPF overlay maps validated layouts locally. Same-user, visible-confirmed still PNG snapshots use bounded GDI/PrintWindow capture and a fixed Pictures subdirectory; tests use a fake backend. |
| RTSS / native WGC video, Media Foundation, WASAPI capture | Blocked | Discovery and capture orchestration exist, but no RTSS output or native video/audio backend is claimed. |

## Updates, release, and qualification

| Plan area | Status | Evidence and remaining boundary |
| --- | --- | --- |
| Driver transaction executor | Partial | Staged-INF containment, exact PnP/catalog/publisher/hash checks, OEM rollback export, journal, reboot sentinel, and fake-package tests exist. A signed service plus an exact vendor package and physical rollback trial are required before use. |
| Firmware / BIOS workflow | Blocked | Exact-package/recovery preflight exists. Firmware and BIOS writing remain intentionally unavailable. |
| Signed app, service, installer, and Game Bar release | Blocked | Build scripts require and validate a Code Signing certificate. This machine has no usable production certificate. |
| SBOM, attestations, adapter-pack distribution | Partial | Release pipeline hooks exist; a signed public release has not been produced. |
| 18-system / two-report 1.0 matrix | Blocked | The qualification ledger and release gate are implemented. The current ledger has no signed physical-system record, so it correctly blocks 1.0. |

## What this audit means

The supplied plan is **not fully complete**. It cannot be truthfully completed on one unsigned reference machine because it requires vendor SDK/licence review, exact production signing, and physical hardware validation across the published matrix. The safe source implementation can expand recognition and read-only diagnostics broadly; any new write capability still needs an audited adapter and exact-device evidence.

See [compatibility-catalog.md](compatibility-catalog.md) for the read-only family recognition layer, [feature-status.md](feature-status.md) for the implementation matrix, and [competitor-parity.md](competitor-parity.md) for the clean-room feature comparison.

# RigPilot — next-steps prompt (updated 2026-07-16, after beta7)

Copy everything below the line into a fresh Claude Code session started in
`C:\Users\melis\OneDrive\Documents\pchelper`.

---

You are continuing RigPilot (internal name PCHelper), a GPL-3.0 Windows hardware-control
suite. Before doing anything, read `AI_CONTEXT.md` end to end (the safety rules are
non-negotiable) and the "Status ledger" section of `docs/beta-roadmap.md`.

**Current state:** the deployed LocalAlpha service runs `0.4.0-alpha-20260716-beta7`.
The full test suite passes (542 tests, 0 warnings) and the UI automation smoke is green.
Everything buildable without new hardware, external services, or a human witness is
built — including the arm-gated GPU write families, the transport-less CPU PBO
scaffolding, the pack SDK, portable mode, and the localization pipeline.
`Test-ReleaseReadiness.ps1` shows zero blocking code defects; 1.0 is gated purely on
evidence that must never be fabricated.

Work the phases in order. Each numbered step is one sitting; stop and ask whenever a
step needs my hands, my money, or my decision.

## Phase 1 — Witnessed live passes (I'm at the machine; record each as an AI_CONTEXT snapshot)
1. Kraken X3 telemetry with NZXT CAM fully closed (`Devices → Read cooler status`).
   AccessDenied with CAM open is the designed result, not a failure.
2. One small arm-gated write per GPU family — fan duty, power limit, clock offset:
   arm with exact-device confirmation, apply, verify read-back, disarm, confirm
   defaults restored. Never suggest a voltage change; that path does not exist.
3. Install RivaTuner Statistics Server, then run the RTSS passes: OSD publish/release,
   frame stats against a real game, and one frame-rate benchmark.
4. A short WGC test recording and a PNG snapshot.
5. Start the 24-hour soak (service + agent up, normal cadence) and check working-set
   growth at the end.

## Phase 2 — Code signing (the keystone; nothing else on this list matters until it's picked)
Present the two routes with current pricing, then execute my choice:
- **SignPath Foundation** — free for OSS, requires the repo public on GitHub.
- **Azure Trusted Signing** — ~$9.99/mo, repo stays private.
Then: wire signing into `publish.ps1`/`build-installer.ps1`, produce the first signed
build, re-run `Test-ReleaseReadiness.ps1` (CanPublishSignedAlpha must flip true), and
capture qualification record **#1 of 18** on this rig with
`pchelper-cli qualification-draft` — using only the genuinely witnessed Phase 1 results.

## Phase 3 — The 18-system matrix
Launch the community program: public repo (if not already), signed public beta,
record-submission flow with signature verification, public ledger site, and the lab
acquisition list (one RDNA GPU, one Arc GPU, one Intel 12th+ system, MSI/Gigabyte/ASRock
boards, one Corsair + one Lian Li device). Mostly waiting on hardware and volunteers —
build the tooling so records can arrive.

## Phase 4 — Code that can still be built any time I say go (no hardware needed)
File/plugin sensor inputs (rate-limited, never command hardware) · GOG/Xbox/Battle.net
manifest scanning · game-mode one-toggle UX · hotkey overlay palette · sensor-tree
parity view · replay-buffer capture · drag-editable curve points + template library ·
bulk string extraction into the localization pipeline · accessibility audit completion.

## Phase 5 — Gated items (open only when their gate opens)
Live PBO/CO writes (gate: `docs/qualification/cpu-tuning-and-intel-arc.md` — the
scaffolding and boot sentinel already exist, deliberately transport-less) · Aura
SMBus / Polychrome / RGB Fusion native writes · ADLX/IGCL telemetry on real hardware ·
Kraken pump control after pump qualification · driver-update live run · report-api
deployment · auto-update delivery.

## Standing rules (verbatim from AI_CONTEXT.md — do not soften)
Signed PawnIO or documented vendor APIs only; never WinRing0. Never increase voltage
automatically. Never commit, push, publish, deploy, or install drivers unless I ask.
Tests for every transaction/safety/IPC/capability change; 0 warnings. Update
AI_CONTEXT.md verification snapshots after material changes. Keep public language
precise. **No qualification record may be fabricated — 1.0 ships when the evidence is
real.**

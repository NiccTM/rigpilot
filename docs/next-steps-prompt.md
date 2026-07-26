# RigPilot — next-steps prompt (updated 2026-07-24, after the fan-control pass)

Copy everything below the line into a fresh Claude Code session started in
`C:\Dev\pchelper`.

---

You are continuing RigPilot (internal name PCHelper), a GPL-3.0 Windows hardware-control
suite. Before doing anything, read `AI_CONTEXT.md` end to end (the safety rules are
non-negotiable) and the "Status ledger" section of `docs/beta-roadmap.md`.

**Current state:** the source line is `0.7.0`; the deployed LocalAlpha service runs
`0.7.0-alpha-20260724-130458`. The full suite passes (477 core + 568 integration, 0
warnings) and the UI automation smoke is green. The repository is public at
<https://github.com/NiccTM/rigpilot>, which satisfied the SignPath Foundation
prerequisite — **the signing route decision itself is still open, and it is the keystone.**

Everything buildable without new hardware, external services, or a human witness is
built. `Test-ReleaseReadiness.ps1` shows zero blocking code defects; 1.0 is gated purely
on evidence that must never be fabricated, plus code signing.

Since the last revision of this document: GPU fan, power, and clock each run in their own
NVAPI helper child, released while disarmed; the fan-control write-lock chain is fixed and
live-verified; hardware control arms automatically; GPU and case fans accept a firmware
zero-RPM idle while a pump or CPU fan never is; and the dashboard has an optional per-user
start-at-sign-in setting.

Work the phases in order. Each numbered step is one sitting; stop and ask whenever a step
needs my hands, my money, or my decision.

## Phase 1 — Code signing (the keystone; eleven other items sit behind it)

Nothing else on the 1.0 path can advance without it. Signing gates the signed-package
takeover tests, the driver-update executor (which *requires* a signed service), Game Bar
MSIX production packaging, SBOMs and attestations, and **every qualification record** —
records must be signed, so the 18-system matrix cannot even start at record #1.

Present the two routes with current pricing, then execute my choice:

- **SignPath Foundation** — free for OSS; its public-repo prerequisite is already met.
- **Azure Trusted Signing** — ~$9.99/mo; its advantage was keeping the repo private, which
  no longer applies.

The pipeline already supports staged SignPath signing of the runtime components, MSI, and
bundle, followed by Authenticode verification and runtime hash re-verification. This is an
application to submit, not a system to build. Then: produce the first signed build, re-run
`Test-ReleaseReadiness.ps1` (`CanPublishSignedAlpha` must flip true), and capture
qualification record **#1 of 18** with `pchelper-cli qualification-draft` — using only
genuinely witnessed results.

## Phase 2 — Witnessed live passes (I'm at the machine; record each as an AI_CONTEXT snapshot)

1. Kraken X3 telemetry with NZXT CAM fully closed (`Devices → Read cooler status`).
   AccessDenied with CAM open is the designed result, not a failure.
2. GPU fan, power limit, and clock offset each have a live apply/verify/restore pass
   recorded. What remains is **repeated-cycle qualification**: restart testing,
   suspend/resume, GPU-driver restart, and unexpected-reboot recovery. Experimental
   evidence is not full qualification. Never suggest a voltage change; that path does not
   exist.
3. Install RivaTuner Statistics Server, then run the RTSS passes: OSD publish/release,
   frame stats against a real game, and one frame-rate benchmark.
4. A short WGC test recording and a PNG snapshot.
5. The 24-hour memory-growth soak (`scripts\Measure-RuntimeFootprint.ps1`).

## Phase 3 — Footprint (no hardware, no money, currently the only stated release blocker I can attack)

The 10-minute closed-dashboard soak measured 388–394 MB combined working set against a
200 MB target. The CPU (0.458%) and network (zero unexpected connections) targets passed.
Releasing the GPU session helpers while disarmed already cut into this; the remaining gap
is real work, and it is measurable without anyone's help.

## Phase 4 — The 18-system matrix

Launch the community program: signed public beta, record-submission flow with signature
verification, public ledger site, and the lab acquisition list (one RDNA GPU, one Arc GPU,
one Intel 12th+ system, MSI/Gigabyte/ASRock boards, one Corsair + one Lian Li device).
Mostly waiting on hardware and volunteers — build the tooling so records can arrive.

## Phase 5 — Code that can still be built any time I say go (no hardware needed)

GOG/Xbox/Battle.net manifest scanning · game-mode one-toggle UX · hotkey overlay palette ·
sensor-tree parity view · replay-buffer capture · drag-editable curve points + template
library · bulk string extraction into the localization pipeline · accessibility audit
completion. (File and plugin sensor inputs shipped 2026-07-16.)

## Phase 6 — Gated items (open only when their gate opens)

Live PBO/CO writes (gate: `docs/qualification/cpu-tuning-and-intel-arc.md` — the
scaffolding and boot sentinel already exist, deliberately transport-less) · Aura SMBus /
Polychrome / RGB Fusion native writes · ADLX/IGCL telemetry on real hardware · Kraken pump
control after pump qualification · driver-update live run · report-api deployment ·
auto-update delivery.

## Traps recorded from live sessions — do not re-derive these

- **Deploying needs `-ServiceTimeoutSeconds 90` on this machine.** `state.db` is ~247 MiB
  and the pre-start backup copies it, so the default 45-second handshake window expires.
  That message is not a payload defect, and the auto-rollback that follows it is correct.
- **Wait ~15 s before judging cooling state after a mode switch.** `status` legitimately
  reports the previous `cooling.graphId` while `activeProfileId` already shows the new
  profile; the apply holds the cooling gate through the fan read-back settle window.
- **Only one service-owned cooling graph is active at a time.** Activating a GPU-fan mode
  releases the case fans to firmware, and vice versa. That is by design, not a bug.
- **A non-elevated direct run of the service exe reports `SQLite Error 8: attempt to write
  a readonly database`.** That is the interactive user hitting a LocalSystem-owned
  database, not a startup fault.

## Standing rules (verbatim from AI_CONTEXT.md — do not soften)

Signed PawnIO or documented vendor APIs only; never WinRing0. Never increase voltage
automatically. Never commit, push, publish, deploy, or install drivers unless I ask. Tests
for every transaction/safety/IPC/capability change; 0 warnings. Update AI_CONTEXT.md
verification snapshots after material changes. Keep public language precise. **No
qualification record may be fabricated — 1.0 ships when the evidence is real.**

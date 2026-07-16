# RigPilot — next-steps prompt (written 2026-07-16, after beta5)

Paste everything below the line into a fresh Claude Code session started in
`C:\Users\melis\OneDrive\Documents\pchelper` to continue the road to 1.0.

---

Read AI_CONTEXT.md and docs/beta-roadmap.md (especially the "Status ledger —
2026-07-16" section) before doing anything. Current state: the 0.5–0.9 software
line is complete (475/475 tests, payload `0.4.0-alpha-20260716-beta5` deployed
locally), and `scripts/Test-ReleaseReadiness.ps1` reports zero blocking defects
— 1.0 is gated only on evidence that must never be fabricated. Work through
these phases in order, stopping whenever a step needs my hands or my decision:

## Phase 1 — Witnessed live passes on the deployed beta5 (I'm at the machine)
Walk me through each, one at a time, and record the outcome as an AI_CONTEXT
verification snapshot after each pass:
1. Kraken X3 telemetry with NZXT CAM closed (expect real temp/rpm/duty; with
   CAM open the designed AccessDenied refusal is the correct result).
2. `ryzen-smu-feasibility` via the deployed CLI — live PPT/TDC/EDC/THM numbers
   for the 5800X.
3. One arm-gated GPU write each (fan duty, power limit, clock offset): arm with
   confirmation, apply a small safe value, verify, disarm, confirm defaults
   restored. Never touch voltage.
4. RTSS suite if RivaTuner Statistics Server is installed (it was not on
   2026-07-15): OSD publish/release, frame stats, and a frame-rate benchmark
   against a real game. If RTSS is still absent, note it and move on.
5. A short test recording and PNG snapshot.
6. Start the 24-hour soak (service + agent running, snapshot cadence normal)
   and schedule the follow-up check.

## Phase 2 — Code signing (the keystone for the 1.0 evidence matrix)
I need to pick one; present the trade-offs again briefly, then execute my pick:
- SignPath Foundation (free for OSS, requires the repo public on GitHub), or
- Azure Trusted Signing (~$9.99/mo, no public-repo requirement).
Once credentials exist: integrate signing into the publish pipeline, produce
the first signed build, re-run `Test-ReleaseReadiness.ps1` (CanPublishSignedAlpha
should flip to true), and record THIS machine (X570-E / 5800X / RTX 3090 /
Kraken X3) as qualification record #1 of 18 using the real witnessed results
from Phase 1.

## Phase 3 — The 18-system qualification matrix
Stand up the community qualification program from the roadmap: public GitHub
repo (if not already public from Phase 2), signed public beta download,
qualification-record submission flow with signature verification, and the
lab/community hardware acquisition list covering Zen 3/4/5, Intel 12th/13–14th/
Core Ultra 200, RTX 30/40/50, RX 6000/7000/9000, Arc A/B, and 4 board vendors
on both platforms. This phase is mostly waiting on hardware and volunteers;
build the tooling and docs so records can arrive.

## Phase 4 — Remaining gated roadmap items (only as their gates open)
Shipped 2026-07-16 (in payload beta6, suite 517 tests): the gated PBO tuning
scaffolding (transport seam + boot-recovery sentinel + arm gate, all inert and
Blocked until qualification), the ADLX feasibility detector, the adapter-pack
SDK (docs/adapter-pack-sdk.md + template + New-AdapterPack.ps1), the
qualification-draft evidence flow (pchelper-cli qualification-draft), portable
mode (--portable), and localization scaffolding (--culture, docs/localization.md).
Still gated on hardware or external services:
- Live PBO/CO writes: full gate in docs/qualification/cpu-tuning-and-intel-arc.md.
- Aura SMBus native writes, ADLX/IGCL telemetry, Polychrome/RGB Fusion,
  peripheral packs, AIO pump control: need the listed hardware.
- Pack signing service, community ledger site, report-api, auto-update
  delivery: need signing enrolment and hosting decisions.
- Bulk string extraction into the localization pipeline (mechanical, any time).

## Standing rules (unchanged)
Signed PawnIO or documented vendor APIs only; never auto-increase voltage;
never commit/push/publish/deploy/install drivers unless I ask; tests for every
transaction/safety/IPC/capability change; update AI_CONTEXT.md snapshots; no
fabricated qualification records — 1.0 ships when the evidence is real.

# ADR 0002: Capability-gated hardware writes

Status: Accepted

An adapter may expose monitoring without exposing writes. A write becomes available only when it supplies bounded metadata plus prepare, apply, verify, rollback, and reset operations. Reverse-engineered endpoints are always Experimental until the published hardware matrix is satisfied.

Experimental operations require a global advanced-write acknowledgement and an exact-device acknowledgement. Native calls run in the restartable Adapter Host. Calibration and tuning persist a boot sentinel before writing, accept cancellation, and restore the previous or firmware/default state before completing. A failed restore blocks subsequent writes.

`Prepare` is a required no-write eligibility gate and must pass in the exact execution context that would issue the write. A direct elevated interactive diagnostic cannot substitute for a failed LocalSystem Adapter Host preflight. On a preflight failure, the commissioning record becomes `Failed` and cannot be retried into a physical pulse; a new session is required only after the adapter or execution-context defect has been corrected. The implemented user-session diagnostic uses explicit UAC plus a random private-pipe token, but it exposes `Prepare` only and is not a hardware-control carrier. Any future typed-write bridge must use independent authentication, the same explicit UAC model, and a new complete apply/read-back/rollback/reset qualification path.

Physical cooling roles are persisted as service-owned records keyed to the exact control capability. A stored CPU-fan role forbids identification pulses and zero-RPM commands. A stored pump role is stricter: it forbids commissioning pulses, calibration, cooling-graph outputs, profile actions, and automatic tuning until an exact controller-specific pump qualification establishes a safe nonzero operating policy. Removing a CPU-fan or pump role requires an explicit operator acknowledgement and cannot be accomplished by a dashboard-only state change.

Automatic tuning is single-domain and never adjusts voltage. Manual positive voltage is permitted only within adapter-reported exact-device bounds, after a fresh per-session acknowledgement, and never from startup, automation, built-in profiles, or automatic tuning. Its successful final result is labelled `Passed 10-minute screening` and remains Provisional; it is not called stable.

Imported adapter packs are out-of-process only. Production packs require an enrolled Ed25519 publisher key, matching protocol range, declared permissions, exact hardware identifiers, and verified payload hashes. Debug hash trust is explicit and cannot authorize firmware updates.

RigPilot will not bundle WinRing0 or advise users to disable Windows security. Signed PawnIO is the preferred low-level access path when required.

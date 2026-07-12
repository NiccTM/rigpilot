# ADR 0002: Capability-gated hardware writes

Status: Accepted

An adapter may expose monitoring without exposing writes. A write becomes available only when it supplies bounded metadata plus prepare, apply, verify, rollback, and reset operations. Reverse-engineered endpoints are always Experimental until the published hardware matrix is satisfied.

PC Helper will not bundle WinRing0 or advise users to disable Windows security. Signed PawnIO is the preferred low-level access path when required.

# Architecture

RigPilot separates policy from hardware access.

```text
WPF dashboard / tray / CLI
       |                 |
 user-agent pipe    service pipe v2
       |                 |
per-user SQLite    RigPilot Service ---- ProgramData SQLite/history
       |                 |
Automation / Effect Hosts   job-isolated Adapter Host
 scripts / WebView2 FX             |
                      reviewed adapters
```

The service is the hardware transaction and safety coordinator. It owns capabilities, hardware profiles, cooling graphs, persisted physical cooling-output roles, adapter packs, ownership consent, operations, revisions, idempotency, rollback journals, and boot recovery. LibreHardwareMonitor runs in a restartable Adapter Host reached through a unique private pipe. Every host request carries a random per-launch token, and the child is assigned to a kill-on-close Windows job object. A successful `Prepare` must occur in the same execution context that would own a write: a direct elevated interactive diagnostic is not evidence that the LocalSystem child can control the same endpoint. A LocalSystem preflight failure closes the commissioning session before any physical command. A CPU-fan/pump role is enforced by the service rather than the dashboard; a pump role blocks all direct output control until a device-specific qualification path exists. The user agent has a separate explicit-UAC, token-authenticated diagnostic child that can run `Prepare` only and returns through a private pipe; it cannot apply, verify, roll back, reset, or transfer authority to the service. No user-session typed-write bridge exists, so motherboard fan writes remain blocked until that distinct bridge independently passes its own gate.

The non-elevated dashboard/tray also hosts the user-agent pipe and owns per-user workflows, lighting/effects, games, macros, scripts, OSD layouts, capture presets, OpenRGB, and Dynamic Lighting. Scripts execute only in Automation Host after a file-hash recheck. Trusted JavaScript effects execute only in Effect Host after entry-point hash revalidation; WebView2 host objects, external resources, navigation, downloads, permissions, and new windows are denied. OpenRGB is restricted to loopback port 6742 and requires explicit opt-in. The Windows service has no network client.

Sensor history uses normalised SQLite metadata. Operational samples are retained at 5-second resolution for 24 hours, then converted transactionally to one-minute averages for 30 days. Retention also enforces a 250 MB database/WAL cap. Full 1-second samples remain in memory for live views and are not all persisted.

## Invariants

1. Discovery and reads precede every write capability.
2. A write capability must declare bounds, risk, execution context, evidence, and reset semantics.
3. Profile mutations are typed and revision-checked.
4. A pending transaction is durable before the first write.
5. Failure rolls back applied actions in reverse order.
6. Experimental profiles are not automatically restored after an unclean shutdown.
7. Stale critical sensors cause an emergency cooling decision and an attempted return to firmware control.
8. Calibration and tuning operate on one bounded capability at a time and restore the previous state on success, failure, or cancellation.
9. A pending operation is durable before its first write and is never reapplied after an unclean boot.
10. Automatic voltage adjustment is rejected before adapter preparation.
11. Competing writers convert only overlapping write capabilities to `Blocked`. Takeover coordination must revalidate stored consent across normalized path, product, publisher, signer, and SHA-256 immediately before any platform executor mutates state.
12. Protocol-1 clients remain read-only. Protocol-mismatched clients cannot mutate.
13. Adapter packs are bounded archives whose manifest signature and every declared payload hash verify before staging.
14. User scripts and effects never execute in LocalSystem or inside the hardware service.

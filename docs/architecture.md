# Architecture

PC Helper separates policy from hardware access.

```text
WPF dashboard / tray / CLI
            |
   versioned named pipe
            |
    PC Helper Service  ---- SQLite state/history
            |
       Adapter Host
            |
  reviewed built-in adapters
```

The service is the transaction and safety coordinator. It owns profiles, cooling policy, state revisions, idempotency, and rollback journals. The adapter host isolates native interop failures. The dashboard remains non-elevated and performs report uploads and update checks; the service has no network client.

Sensor history uses normalised SQLite metadata. Operational samples are retained at 5-second resolution for 24 hours, then converted transactionally to one-minute averages for 30 days. Retention also enforces a 250 MB database/WAL cap. Full 1-second samples remain in memory for live views and are not all persisted.

## Invariants

1. Discovery and reads precede every write capability.
2. A write capability must declare bounds, risk, execution context, evidence, and reset semantics.
3. Profile mutations are typed and revision-checked.
4. A pending transaction is durable before the first write.
5. Failure rolls back applied actions in reverse order.
6. Experimental profiles are not automatically restored after an unclean shutdown.
7. Stale critical sensors cause an emergency cooling decision and an attempted return to firmware control.
